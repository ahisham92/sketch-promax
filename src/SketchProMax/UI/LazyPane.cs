using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SketchProMax.UI;

/// <summary>
/// A tab of the loader's dockable pane (CoreEntry.PaneContent) whose real content is made at Revit's next idle moment
/// rather than when the loader asks: with the pane open at start, the loader asks while Revit is still starting or
/// opening a model, and the chat reads its saved conversation and lays out every message. Until then (a moment) it
/// shows a short "Loading" line. Clicking or tabbing into it makes the content at once.
/// </summary>
internal sealed class LazyPane : ContentControl
{
    private static readonly Dictionary<string, LazyPane> Hosts = new(StringComparer.Ordinal);

    private readonly Func<FrameworkElement> _make;
    private bool _filled, _scheduled;

    private LazyPane(string name, Func<FrameworkElement> make)
    {
        _make = make;
        Brand.UseTheme(this);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12) };
        var text = Brand.Text(name == "chat" ? "Loading the chat…" : "Loading…", Brand.K.Muted, 12);
        row.Children.Add(Brand.Fg(new ContentControl { Content = Brand.Spinner(), VerticalAlignment = VerticalAlignment.Center }, Brand.K.Muted));
        row.Children.Add(text);
        AutomationProperties.SetName(this, text.Text);
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        Content = row;
        Focusable = false;
        PreviewMouseDown += (_, _) => Fill();
        IsKeyboardFocusWithinChanged += (_, _) => Fill();
    }

    /// <summary>The host for pane tab <paramref name="name"/>; its content is made by <paramref name="make"/> at the next idle moment.</summary>
    public static FrameworkElement For(string name, Func<FrameworkElement> make)
    {
        if (!Hosts.TryGetValue(name, out var host) || host._filled && host.Content is null) Hosts[name] = host = new LazyPane(name, make);
        host.Schedule();
        return host;
    }

    /// <summary>Makes tab <paramref name="name"/>'s content now, when the loader has asked for it (Send view, for one).</summary>
    public static void FillNow(string name)
    {
        if (Hosts.TryGetValue(name, out var host)) host.Fill();
    }

    /// <summary>The core is stopping: the next start (or the next core) makes its own hosts.</summary>
    public static void Forget()
    {
        foreach (var host in Hosts.Values)
            if (!host._filled) Brand.ReleaseTheme(host);
        Hosts.Clear();
    }

    private void Schedule()
    {
        if (_filled || _scheduled) return;
        _scheduled = true;
        App.Dispatcher.Later(Fill);
    }

    private void Fill()
    {
        if (_filled) return;
        _filled = true;
        FrameworkElement element;
        try { element = _make(); }
        catch (Exception ex)
        {
            Content = Brand.Text("This tab could not be shown: " + ex.Message + " Restart Revit to try again.", Brand.K.Text, 12);
            return;
        }
        // Held by an earlier host (this core started again after a failed switch): take it over.
        if (element.Parent is ContentControl holder && !ReferenceEquals(holder, this)) holder.Content = null;
        Content = element;
        Brand.ReleaseTheme(this);
        AutomationProperties.SetName(this, "");
    }
}
