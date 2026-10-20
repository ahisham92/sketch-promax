using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using SketchProMax.Tools;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace SketchProMax.UI;

/// <summary>
/// Checking rules (Pre-submission checklist > Project details > Checking rules…): what the automatic model test
/// compares the model with, such as the level and scope box name patterns, the approved scales and worksets, the
/// classification and IFC parameters. Kept with the checklist (ChecklistStore) and in its Excel file's Rules sheet, so
/// the office shares them. A rule left empty turns that test off and the check is done by hand.
/// </summary>
internal sealed class ChecklistRulesWindow : Window
{
    private readonly ChecklistMath.Definition _def;
    private readonly Dictionary<string, TextBox> _boxes = new(StringComparer.Ordinal);
    private readonly TextBlock _error = Brand.Text("", Brand.K.DangerText, 12);

    /// <summary>The checklist with its new rules after Save; null when cancelled.</summary>
    public ChecklistMath.Definition? Saved { get; private set; }

    public static ChecklistMath.Definition? Edit(Window owner, ChecklistMath.Definition def)
    {
        var window = new ChecklistRulesWindow(def) { Owner = owner };
        window.ShowDialog();
        return window.Saved;
    }

    /// <summary>The window on <paramref name="def"/>, not shown (Test before sharing builds it).</summary>
    internal static Window ForTest(ChecklistMath.Definition def) => new ChecklistRulesWindow(def);

    private ChecklistRulesWindow(ChecklistMath.Definition def)
    {
        _def = def;
        Title = "Sketch ProMax checking rules";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(760, area.Width * 0.9);
        Height = Math.Min(760, area.Height * 0.9);
        MinWidth = 520;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Brand.Prepare(this);
        var motion = RichUi.MotionOf(Settings.AgentSettings.Load());
        RichUi.Apply(this, motion);

        var page = new StackPanel();
        page.Children.Add(Para("These are what the automatic model check compares the model with. Patterns: # is a digit, ? any one character, * anything; " +
                               "separate several with commas. Leave a rule empty to turn that test off: its check is then done by hand.", Brand.K.Muted));
        page.Children.Add(Para("NWC and IFC files don't record their export settings: checks 49 and 50 pass for the files Sketch ProMax exports itself (Fix it on those checks), with the office settings.", Brand.K.Muted));
        foreach (var rule in ChecklistMath.RuleDefs)
        {
            var label = Brand.Text(rule.Label, Brand.K.Text, 13, bold: true);
            label.Margin = new Thickness(0, 14, 0, 0);
            page.Children.Add(label);
            var checks = def.Items.Where(i => i.Auto == rule.Check).Select(i => "#" + i.Number + " " + i.Title).ToList();
            page.Children.Add(Para(rule.Help + (checks.Count > 0 ? " Used by " + string.Join("; ", checks) + "." : ""), Brand.K.Muted, 11.5));
            var box = new TextBox { MinHeight = 28, Margin = new Thickness(0, 4, 0, 0), VerticalContentAlignment = VerticalAlignment.Center, Text = ChecklistMath.Rule(def, rule.Key) };
            AutomationProperties.SetName(box, rule.Label);
            AutomationProperties.SetHelpText(box, rule.Help);
            _boxes[rule.Key] = box;
            page.Children.Add(box);
        }
        var scroll = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(18, 12, 18, 12) };

        var defaults = RichUi.Button(RichUi.Icon.Reset, "Back to the defaults");
        defaults.Click += (_, _) => { foreach (var rule in ChecklistMath.RuleDefs) _boxes[rule.Key].Text = rule.Default; };
        var cancel = RichUi.Button(RichUi.Icon.Close, "Cancel");
        cancel.IsCancel = true;
        var save = RichUi.Button(RichUi.Icon.Save, "Save", Brand.Weight.Primary);
        save.IsDefault = true;
        save.Click += (_, _) => Save();
        var buttons = Brand.ButtonRow(defaults, cancel, save);
        buttons.Margin = new Thickness(0);
        var foot = new DockPanel { Margin = new Thickness(18, 10, 18, 14) };
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.Add(buttons);
        _error.TextWrapping = TextWrapping.Wrap;
        _error.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        foot.Children.Add(_error);

        var root = new DockPanel();
        var header = Brand.Header("Checking rules", def.Title);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(foot);
        root.Children.Add(scroll);
        Content = root;
        Loaded += (_, _) => { if (_boxes.Count > 0) _boxes.Values.First().Focus(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { Save(); e.Handled = true; } };
    }

    private void Save()
    {
        var size = _boxes["filesize"].Text.Trim();
        if (size.Length > 0 && !(double.TryParse(size, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mb) && mb > 0))
        {
            _error.Text = "Largest file size is a number of MB, such as 300 (or empty).";
            _boxes["filesize"].Focus();
            return;
        }
        var def = ChecklistMath.FromJson<ChecklistMath.Definition>(ChecklistMath.ToJson(_def)) ?? _def;
        def.Rules = new Dictionary<string, string>(StringComparer.Ordinal);
        // Only what differs from the default is kept, so a later default change still reaches the rest.
        foreach (var rule in ChecklistMath.RuleDefs)
        {
            var value = _boxes[rule.Key].Text.Trim();
            if (value != rule.Default) def.Rules[rule.Key] = value;
        }
        ChecklistMath.MarkChangedHere(def);
        try { ChecklistStore.SaveInUse(def); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _error.Text = $"Couldn't save the rules ({ex.Message}).";
            return;
        }
        Saved = def;
        Close();
    }

    private static TextBlock Para(string text, string key, double size = 12)
    {
        var t = Brand.Text(text, key, size);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, 4, 0, 0);
        return t;
    }
}
