using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SketchProMax.Update;

namespace SketchProMax.UI;

/// <summary>
/// "Restart Revit to see the new buttons and pane", when part of this version can't appear until Revit restarts: the
/// loader (0.30 on) says what it could not add live ("restartnote" query); an older loader says nothing, and this core
/// works it out from the loader's release (<see cref="LoaderCompat"/>). Shown in the update window, on the ribbon's
/// Version button and at the top of the chat.
/// </summary>
internal static class RestartNote
{
    private static string? _fromLoader;
    private static bool _loaderSpoke;

    /// <summary>Raised on the UI thread when the note changes.</summary>
    public static event Action? Changed;

    /// <summary>The note, or null when everything this version brings is on screen.</summary>
    public static string? Text
    {
        get
        {
            if (_fromLoader is not null) return _fromLoader;
            // A loader that answers "restartnote" (0.30 on) has said all there is; a running core without the loader
            // (an old manifest) has its own fixed buttons and nothing to restart for.
            if (_loaderSpoke || !Updater.CanSwitchLive) return null;
            return LoaderCompat.RibbonOutdated(AppDomain.CurrentDomain.GetData(LoaderCompat.ReleaseKey) as string) ? LoaderCompat.OldLoaderNote : null;
        }
    }

    /// <summary>From the loader after it applied this core's manifest: what needs a restart ("" for nothing).</summary>
    public static void FromLoader(string note)
    {
        _loaderSpoke = true;
        _fromLoader = note.Trim().Length == 0 ? null : note.Trim();
        // The loader now knows what is missing; if it is older than this core's ribbon, the release check still applies.
        if (_fromLoader is null && LoaderCompat.RibbonOutdated(AppDomain.CurrentDomain.GetData(LoaderCompat.ReleaseKey) as string))
            _fromLoader = LoaderCompat.OldLoaderNote;
        Changed?.Invoke();
    }

    /// <summary>
    /// After this core started in place of another one in the same Revit session, under a loader that doesn't know about
    /// restart notes (before 0.30): the previous core's update window says "no restart needed", so replace it with this
    /// core's own, which says what needs a restart. Runs after the loader has finished the switch.
    /// </summary>
    public static void AfterStart(Dispatcher ui)
    {
        if (_loaderSpoke || Text is not { } note || !SwitchedInSession()) return;
        ui.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            // The loader may have spoken meanwhile (it asks after Start); it then sees to the window itself.
            if (_loaderSpoke || Text is null) return;
            try { CloseOtherCoresUpdateWindows(); }
            catch (Exception) { /* two windows at worst */ }
            UpdateWindow.Announce(new UpdateSnapshot(UpdateStage.Updated, Updater.CurrentText, 0, null, Text, DateTime.UtcNow));
        }));
    }

    /// <summary>True when another version of the core was loaded in this Revit process before this one.</summary>
    private static bool SwitchedInSession()
    {
        var own = typeof(RestartNote).Assembly;
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies().Any(a =>
                !ReferenceEquals(a, own) && !a.IsDynamic && a.GetName().Name == own.GetName().Name && a.GetName().Version != own.GetName().Version);
        }
        catch (Exception) { return false; }
    }

    private static void CloseOtherCoresUpdateWindows()
    {
        var mine = typeof(UpdateWindow);
        foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>().ToList())
            if (source.RootVisual is Window w && w.GetType().FullName == mine.FullName && w.GetType() != mine) w.Close();
    }
}
