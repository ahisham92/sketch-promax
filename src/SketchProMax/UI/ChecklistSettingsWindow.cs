using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SketchProMax.Settings;
using SketchProMax.Tools;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;

namespace SketchProMax.UI;

/// <summary>
/// The checklist's own settings, in one place (the gear in the checklist's header): running the checks, the helper for
/// the checks the model can't settle, the checklist and its checking rules, and the signatures. The helper is saved as
/// soon as it is picked; the other buttons close this window and open what they name.
/// </summary>
internal sealed class ChecklistSettingsWindow : Window
{
    /// <summary>What the checklist window does for the buttons here.</summary>
    public sealed record Actions(Action RunQuick, Action RunSlow, Action EditChecklist, Action EditRules,
        Action<string> UseForProject, Action NewChecklist, Action ImportForProject, Action Export, Action DeleteProjectChecklist, Action ChangePassword,
        Action MakeDefault, Action TestBeforeSharing);

    /// <summary>The project the checklist is for, the project checklist it uses ("" for the office one), and those on this PC.</summary>
    public sealed record Project(string Code, string InUse, IReadOnlyList<string> Names);

    /// <summary>Whether the helper was changed (the checklist then redraws its Ask buttons).</summary>
    public bool HelperChanged { get; private set; }

    private Action? _then;

    /// <summary>Shows the settings; returns whether the helper changed. A button's action runs after this window closes.</summary>
    public static bool Edit(Window owner, Actions actions, int automatic, int pending, Project project)
    {
        var window = new ChecklistSettingsWindow(actions, automatic, pending, project) { Owner = owner };
        window.ShowDialog();
        window._then?.Invoke();
        return window.HelperChanged;
    }

    /// <summary>The window, not shown (Test before sharing builds it).</summary>
    internal static Window ForTest(Actions actions, Project project) => new ChecklistSettingsWindow(actions, 40, 5, project);

    private ChecklistSettingsWindow(Actions actions, int automatic, int pending, Project project)
    {
        Title = "Sketch ProMax checklist settings";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(620, area.Width * 0.9);
        Height = Math.Min(720, area.Height * 0.9);
        MinWidth = 460;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Brand.Prepare(this);
        var settings = AgentSettings.Load();
        RichUi.Apply(this, RichUi.MotionOf(settings));

        var page = new StackPanel();

        page.Children.Add(Section("Run the checks"));
        page.Children.Add(Para($"{automatic} checks are tested from the model. Nothing is tested until you press Run checks; the quick checks take seconds. " +
                               (pending > 0 ? $"{pending} slower ones (duplicates, floating elements, clashes, the model check score, the standard template) run only when you ask: they can take minutes on a big model."
                                            : "The slower ones (duplicates, floating elements, clashes, the model check score, the standard template) run only when you ask.")));
        page.Children.Add(Row(
            Do(RichUi.Button(RichUi.Icon.Refresh, "Run the quick checks", Brand.Weight.Primary), actions.RunQuick),
            Do(RichUi.Button(RichUi.Icon.Refresh, "Run all checks, slow ones too"), actions.RunSlow)));

        page.Children.Add(Section("Helper for the checks you do by hand"));
        page.Children.Add(Para("Each check the model can't test gets an Ask button for the helper picked here. The helper only advises: you still answer the check."));
        var current = ChecklistHelper.Parse(settings.ChecklistHelper);
        foreach (var (tag, text) in ChecklistHelper.Choices)
        {
            var choice = new RadioButton { Content = text, GroupName = "helper", IsChecked = tag == current, Margin = new Thickness(0, 6, 0, 0) };
            choice.SetResourceReference(ForegroundProperty, Brand.K.Text);
            choice.Checked += (_, _) =>
            {
                var now = AgentSettings.Load();
                if (ChecklistHelper.Parse(now.ChecklistHelper) == tag) return;
                now.ChecklistHelper = tag;
                now.Save();
                HelperChanged = true;
            };
            page.Children.Add(choice);
        }
        page.Children.Add(Para("Sketch ProMax chat: the check goes to the chat, which looks at the model and says pass or fail. SketchPro: its Copilot opens with the question pasted in, for you to send. None: no Ask buttons.", Brand.K.Muted, 11.5));

        page.Children.Add(Section("The checklist"));
        page.Children.Add(Para("Edit the checklist adds, changes, moves or deletes checks and stages (also through Excel). Checking rules set what the automatic test compares the model with: name patterns, scales, worksets, parameters." +
                               (project.InUse.Length > 0 ? "" : " This project uses the Default checklist, so changing its checks asks for the password; your own Checking rules (your files and folders) don't, and Default updates still reach you.")));
        page.Children.Add(Row(
            Do(RichUi.Button(RichUi.Icon.Edit, "Edit the checklist…"), actions.EditChecklist),
            Do(RichUi.Button(RichUi.Icon.Edit, "Checking rules…"), actions.EditRules)));

        page.Children.Add(Section("Checklist for this project"));
        var code = project.Code.Length > 0 ? project.Code : "this model";
        page.Children.Add(Para(project.InUse.Length > 0
            ? $"Project {code} uses its own checklist, {project.InUse}. Change it freely: Edit the checklist and Checking rules change that one only."
            : $"Project {code} uses the Default checklist. Give it its own (a ports checklist, say): a new checklist starts as a copy of the Default for you to change, or import one exported from another project."));
        page.Children.Add(Para("The Default checklist is the same for everyone and arrives with Sketch ProMax updates; only it is behind the password. " +
                               "Your own checklists are kept on this PC for you only, and updates never overwrite them.", Brand.K.Muted, 11.5));
        var choose = new ComboBox { MinWidth = 260, Margin = new Thickness(0, 10, 8, 8), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(choose, "Checklist this project uses");
        choose.Items.Add(new ComboBoxItem { Content = "The Default checklist", Tag = "" });
        foreach (var name in project.Names) choose.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        choose.SelectedIndex = Math.Max(0, project.InUse.Length == 0 ? 0 : project.Names.ToList().FindIndex(n => string.Equals(n, project.InUse, StringComparison.OrdinalIgnoreCase)) + 1);
        var use = Do(RichUi.Button(RichUi.Icon.Next, "Use for this project"), () => actions.UseForProject((choose.SelectedItem as ComboBoxItem)?.Tag as string ?? ""));
        use.IsEnabled = false;
        choose.SelectionChanged += (_, _) => use.IsEnabled = !string.Equals((choose.SelectedItem as ComboBoxItem)?.Tag as string ?? "", project.InUse, StringComparison.OrdinalIgnoreCase);
        var pickRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        pickRow.Children.Add(choose);
        pickRow.Children.Add(use);
        if (project.Names.Count > 0) page.Children.Add(pickRow);
        var buttons = new List<Button>
        {
            Do(RichUi.Button(RichUi.Icon.Add, "New checklist (a copy of the Default)…"), actions.NewChecklist),
            Do(RichUi.Button(RichUi.Icon.Import, "Import a checklist from Excel…"), actions.ImportForProject),
            Do(RichUi.Button(RichUi.Icon.Export, "Export this checklist to Excel…"), actions.Export),
        };
        if (project.InUse.Length > 0) buttons.Add(Do(RichUi.Button(RichUi.Icon.Delete, $"Delete {project.InUse}", Brand.Weight.Danger), actions.DeleteProjectChecklist));
        page.Children.Add(Row(buttons.ToArray()));

        page.Children.Add(Section("The Default checklist for everyone"));
        page.Children.Add(Para("Make this the Default for everyone saves the checklist shown here as the next Default (password asked). Then ask in the Sketch ProMax project for your Default to be published: it goes into the next update, and every PC gets it when it updates, except for people who changed their Default themselves."));
        page.Children.Add(Para("Test before sharing (password asked) tests the checklist and Share tools in depth on a throwaway project, without changing your models, checklists or shared tools, " +
                               "and saves a report in Documents\\Sketch ProMax\\Test reports to upload in the Sketch ProMax project. It takes a few minutes.", Brand.K.Muted, 11.5));
        page.Children.Add(Row(
            Do(RichUi.Button(RichUi.Icon.Save, "Make this the Default for everyone…"), actions.MakeDefault),
            Do(RichUi.Button(RichUi.Icon.Refresh, "Test before sharing…"), actions.TestBeforeSharing),
            Do(RichUi.Button(RichUi.Icon.Edit, "Change the password…"), actions.ChangePassword)));

        page.Children.Add(Section("Signatures"));
        page.Children.Add(Para("Your name, role and signature picture for the Word and PDF file, and an optional Excel checklist to use instead of the built-in one, are in Sketch ProMax settings, on the Sign-off tab."));
        page.Children.Add(Row(Do(RichUi.Button(RichUi.Icon.Edit, "Open the Sign-off settings…"), () =>
        {
            var window = new SettingsWindow(SettingsLayout.SignOff);
            Brand.Prepare(window);
            window.ShowDialog();
        })));

        var scroll = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(18, 4, 18, 12) };
        var close = RichUi.Button(RichUi.Icon.Close, "Close");
        close.IsCancel = true;
        close.IsDefault = true;
        close.Click += (_, _) => Close();
        var foot = Brand.ButtonRow(close);
        foot.Margin = new Thickness(18, 10, 18, 14);

        var root = new DockPanel();
        var header = Brand.Header("Checklist settings", "Run the checks, the helper, the checklist and its rules, signatures.");
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(foot);
        root.Children.Add(scroll);
        Content = root;
    }

    /// <summary>The button closes this window, then its action runs (editors and readings belong to the checklist window).</summary>
    private Button Do(Button button, Action action)
    {
        button.Margin = new Thickness(0, 0, 8, 8);
        button.Click += (_, _) => { _then = action; Close(); };
        return button;
    }

    private static WrapPanel Row(params Button[] buttons)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    private static TextBlock Section(string text)
    {
        var block = Brand.Text(text, Brand.K.Text, 15, bold: true);
        block.Margin = new Thickness(0, 18, 0, 0);
        return block;
    }

    private static TextBlock Para(string text, string key = Brand.K.Text, double size = 12)
    {
        var block = Brand.Text(text, key, size);
        block.Margin = new Thickness(0, 4, 0, 0);
        return block;
    }
}
