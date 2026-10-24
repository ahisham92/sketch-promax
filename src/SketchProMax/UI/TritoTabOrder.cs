using System.Collections;
using System.Collections.Specialized;
using System.Reflection;
using System.Windows.Threading;
using SketchProMax.Sharing;

namespace SketchProMax.UI;

/// <summary>
/// Moves the TritoRevit tab next to Sketch ProMax's (else SketchPro's, else first of the add-ins; see
/// Sharing/TritoTabMath). Done by the core, which runs with either add-in, so neither byte-stable loader changes.
/// Revit only appends add-in tabs and add-ins load in any order, so it looks again whenever the tab list changes, for
/// the first minute and a half after starting, with a few moves at most (never fighting another add-in that moves
/// tabs). Revit's ribbon control (AdWindows) is reached by reflection: a Revit that changed it keeps its order. Never throws.
/// </summary>
internal static class TritoTabOrder
{
    private const int MaxMoves = 6;
    private static int _moves, _ticks;
    private static bool _started, _moving, _queued, _broken;
    private static DispatcherTimer? _timer;
    private static INotifyCollectionChanged? _hooked;

    /// <summary>Starts looking. Call on Revit's UI thread once the core has started.</summary>
    public static void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += (_, _) =>
            {
                Try();
                if (++_ticks >= 30 || _broken) Stop();
            };
            _timer.Start();
            Try();
        }
        catch (Exception) { _broken = true; }
    }

    private static void Stop()
    {
        try
        {
            _timer?.Stop();
            if (_hooked is not null) _hooked.CollectionChanged -= OnChanged;
            _hooked = null;
        }
        catch (Exception) { }
    }

    private static void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_moving || _queued || _broken) return;
        // The collection can't change inside its own event: look again once it is over.
        _queued = true;
        try { Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { _queued = false; Try(); })); }
        catch (Exception) { _queued = false; }
    }

    private static void Try()
    {
        if (_broken || _moving || _moves >= MaxMoves) return;
        try
        {
            if (Tabs() is not { } tabs) return;
            if (_hooked is null && tabs is INotifyCollectionChanged changes)
            {
                changes.CollectionChanged += OnChanged;
                _hooked = changes;
            }
            var list = new List<(string, string)>();
            foreach (var tab in tabs) list.Add((Text(tab, "Id"), Text(tab, "Title")));
            var to = TritoTabMath.Target(list);
            if (to < 0) return;
            var from = list.FindIndex(t => string.Equals(t.Item1.Trim(), TritoTabMath.Trito, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Item2.Trim(), TritoTabMath.Trito, StringComparison.OrdinalIgnoreCase));
            if (from < 0) return;
            _moves++;
            _moving = true;
            try
            {
                var ribbon = Ribbon();
                var active = ribbon?.GetType().GetProperty("ActiveTab");
                var tab = tabs[from];
                var wasActive = active is not null && ReferenceEquals(active.GetValue(ribbon), tab);
                tabs.RemoveAt(from);
                tabs.Insert(Math.Min(to, tabs.Count), tab);
                if (wasActive) active!.SetValue(ribbon, tab);
            }
            finally { _moving = false; }
        }
        catch (Exception) { _broken = true; Stop(); }
    }

    private static object? Ribbon()
    {
        var manager = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AdWindows")?.GetType("Autodesk.Windows.ComponentManager");
        return manager?.GetProperty("Ribbon", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
    }

    private static IList? Tabs() => Ribbon() is { } ribbon ? ribbon.GetType().GetProperty("Tabs")?.GetValue(ribbon) as IList : null;

    private static string Text(object? tab, string property) => tab?.GetType().GetProperty(property)?.GetValue(tab) as string ?? "";
}
