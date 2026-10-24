using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;

namespace SketchProMax.UI;

/// <summary>
/// Puts the assistant's name (<see cref="BrandText"/>) on what the loader draws itself: the pane's header line
/// ("Claude: connected" → "Sketch ProMax: connected"), its tab and Activity texts, and the ribbon's panel titles
/// (<see cref="CoreRibbonManifest.PanelTitles"/>: Assistant, Submit) and tooltips. The loader doesn't change between releases, so the core rewrites the text where it shows: the header line
/// as soon as the loader sets it (no flicker), the rest of the pane once a second while it is shown, and the ribbon
/// after every manifest and now and then while Revit runs. Elements that belong to the core (the chat and notes tabs)
/// are skipped. Cosmetic: never throws; a loader that changed its layout simply keeps its own words.
/// </summary>
internal static class LoaderBrand
{
    private const string Tab = "Sketch ProMax";

    private static UIApplication? _app;
    private static DispatcherTimer? _timer;
    private static WeakReference<Page>? _page;
    private static readonly List<WeakReference<TextBlock>> Hooked = [];
    // The header line's text, tooltip and accessible name, watched so they are rewritten the moment the loader sets them.
    private static readonly DependencyPropertyDescriptor?[] Watched =
    [
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)),
        DependencyPropertyDescriptor.FromProperty(FrameworkElement.ToolTipProperty, typeof(TextBlock)),
        DependencyPropertyDescriptor.FromProperty(AutomationProperties.NameProperty, typeof(TextBlock)),
    ];
    private static int _ticks;

    public static void Attach(UIApplication app) => _app = app;

    /// <summary>A core tab in the loader's pane: once it is shown, the pane around it is found and kept in line.</summary>
    public static void Watch(FrameworkElement tab)
    {
        tab.Loaded += (_, _) =>
        {
            try
            {
                if (Owner(tab) is not { } page) return;
                _page = new WeakReference<Page>(page);
                Scan(page);
                if (_timer is null)
                {
                    _timer = new DispatcherTimer(DispatcherPriority.Background, tab.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
                    _timer.Tick += (_, _) => Tick();
                    _timer.Start();
                }
            }
            catch (Exception) { /* cosmetic */ }
        };
    }

    /// <summary>The core is stopping: its timer and hooks go with it (the next core makes its own).</summary>
    public static void Stop()
    {
        try
        {
            _timer?.Stop();
            _timer = null;
            foreach (var weak in Hooked)
                if (weak.TryGetTarget(out var block))
                    foreach (var d in Watched) d?.RemoveValueChanged(block, OnText);
            Hooked.Clear();
            _page = null;
        }
        catch (Exception) { /* cosmetic */ }
    }

    /// <summary>The ribbon: the first panel's title, and button texts and tooltips the loader wrote. Revit's thread only.</summary>
    public static void Ribbon()
    {
        if (_app is null) return;
        try
        {
            foreach (var panel in _app.GetRibbonPanels(Tab))
            {
                if (panel is null) continue;
                if (CoreRibbonManifest.PanelTitles.TryGetValue(panel.Name, out var title) && panel.Title != title) panel.Title = title;
                foreach (var item in panel.GetItems())
                {
                    if (item is null) continue;
                    Fix(item);
                    if (item is PulldownButton pulldown)
                        foreach (var inner in pulldown.GetItems())
                            if (inner is not null) Fix(inner);
                }
            }
        }
        catch (Exception) { /* cosmetic */ }
    }

    /// <summary>Called on Revit's thread every couple of seconds (the status button's live label): the ribbon now and then.</summary>
    public static void Pulse()
    {
        if (++_ticks % 15 == 1) Ribbon();
    }

    private static void Fix(RibbonItem item)
    {
        var text = BrandText.Rewrite(item.ItemText);
        if (text != item.ItemText) item.ItemText = text;
        var tip = BrandText.Rewrite(item.ToolTip);
        if (tip != (item.ToolTip ?? "")) item.ToolTip = tip;
        if (item is RibbonButton button)
        {
            var more = BrandText.Rewrite(button.LongDescription);
            if (more != (button.LongDescription ?? "")) button.LongDescription = more;
        }
        else if (item is PulldownButton pulldown)
        {
            var more = BrandText.Rewrite(pulldown.LongDescription);
            if (more != (pulldown.LongDescription ?? "")) pulldown.LongDescription = more;
        }
    }

    private static void Tick()
    {
        try
        {
            if (_page is null || !_page.TryGetTarget(out var page)) { _timer?.Stop(); _timer = null; return; }
            if (page.IsVisible) Scan(page);
        }
        catch (Exception) { /* cosmetic */ }
    }

    private static Page? Owner(DependencyObject element)
    {
        for (DependencyObject? node = element; node is not null;
             node = (node is Visual ? VisualTreeHelper.GetParent(node) : null) ?? LogicalTreeHelper.GetParent(node))
            if (node is Page page) return page;
        return null;
    }

    /// <summary>The loader's elements under <paramref name="node"/> (not the core's own tabs), through the logical tree.</summary>
    private static void Scan(DependencyObject node)
    {
        if (node is LazyPane) return;
        if (node is TextBlock block)
        {
            Set(block);
            // The header line changes with the connection: rewrite it the moment the loader sets it.
            if (block.Text.StartsWith(BrandText.Name + ": ", StringComparison.Ordinal) && Hooked.All(w => !w.TryGetTarget(out var b) || b != block))
            {
                foreach (var d in Watched) d?.AddValueChanged(block, OnText);
                Hooked.Add(new WeakReference<TextBlock>(block));
            }
        }
        if (node is FrameworkElement element)
        {
            if (element.ToolTip is string tip && BrandText.Rewrite(tip) is var newTip && newTip != tip) element.ToolTip = newTip;
            var name = AutomationProperties.GetName(element);
            if (BrandText.Rewrite(name) is var newName && newName != name) AutomationProperties.SetName(element, newName);
            var help = AutomationProperties.GetHelpText(element);
            if (BrandText.Rewrite(help) is var newHelp && newHelp != help) AutomationProperties.SetHelpText(element, newHelp);
        }
        if (node is HeaderedContentControl { Header: string header } headered && BrandText.Rewrite(header) is var h && h != header) headered.Header = h;
        foreach (var child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject d) Scan(d);
    }

    private static void OnText(object? sender, EventArgs e)
    {
        if (sender is not TextBlock block) return;
        Set(block);
        if (block.ToolTip is string tip && BrandText.Rewrite(tip) is var newTip && newTip != tip) block.ToolTip = newTip;
        var name = AutomationProperties.GetName(block);
        if (BrandText.Rewrite(name) is var newName && newName != name) AutomationProperties.SetName(block, newName);
    }

    /// <summary>Rewrites a plain text block (one with links or mixed runs is left alone: setting Text would drop them).</summary>
    private static void Set(TextBlock block)
    {
        if (block.Inlines.Count > 1) return;
        var text = BrandText.Rewrite(block.Text);
        if (text != block.Text) block.Text = text;
    }
}
