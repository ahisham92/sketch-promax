using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SketchProMax.Revit;

/// <summary>
/// A warning before Revit commands that have frozen Revit (FreezeCause.Load: Run systems analysis from the start, and
/// any command the user ticked in the Rescue window after it froze Revit). Each is bound with an AddInCommandBinding
/// whose BeforeExecuted shows the warning; Cancel stops the command, "Run it anyway" lets Revit run it as usual (no
/// Executed handler, so Revit's own command runs). "Don't warn me again" takes it off the list. A command another add-in
/// has bound already is left alone. Bindings are made and removed with the core (a newer core binds them again).
/// </summary>
internal static class HeavyCommands
{
    private static readonly Dictionary<string, AddInCommandBinding> Bound = new(StringComparer.Ordinal);
    private static UIApplication? _app;

    /// <summary>The commands bound now (for the field test).</summary>
    internal static IReadOnlyCollection<string> BoundIds => Bound.Keys;

    /// <summary>Binds the commands on the list. A valid Revit API context.</summary>
    public static void Start(UIApplication app)
    {
        _app = app;
        Apply(app);
    }

    /// <summary>The list changed (a command learned in the Rescue window): bind at Revit's next idle moment. Any thread.</summary>
    public static void Refresh()
    {
        try { _ = App.Dispatcher?.InvokeAsync(app => { Apply(app); return true; }); } catch (Exception) { }
    }

    public static void Stop()
    {
        if (_app is not { } app) return;
        foreach (var pair in Bound.ToList())
        {
            try { pair.Value.BeforeExecuted -= OnBefore; } catch (Exception) { }
            try { app.RemoveAddInCommandBinding(pair.Value.RevitCommandId); } catch (Exception) { }
            Bound.Remove(pair.Key);
        }
        _app = null;
    }

    private static void Apply(UIApplication app)
    {
        if (_app is null) return;
        var want = FreezeCause.Load().Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in want)
        {
            if (Bound.ContainsKey(id)) continue;
            try
            {
                var command = RevitCommandId.LookupCommandId(id);
                if (command is null || !command.CanHaveBinding || command.HasBinding) continue;
                var binding = app.CreateAddInCommandBinding(command);
                binding.BeforeExecuted += OnBefore;
                Bound[id] = binding;
            }
            catch (Exception) { /* bound by another add-in meanwhile, or not bindable in this Revit */ }
        }
        // Taken off the list ("Don't warn me again"): Revit's command runs without asking.
        foreach (var pair in Bound.Where(b => !want.Contains(b.Key)).ToList())
        {
            try { pair.Value.BeforeExecuted -= OnBefore; app.RemoveAddInCommandBinding(pair.Value.RevitCommandId); } catch (Exception) { }
            Bound.Remove(pair.Key);
        }
    }

    private static void OnBefore(object? sender, BeforeExecutedEventArgs e)
    {
        try
        {
            var id = e.CommandId?.Name ?? "";
            if (FreezeCause.Load().FirstOrDefault(w => w.Id == id) is not { } warned) return;
            var (main, content) = FreezeCause.Warning(warned);
            var dialog = new TaskDialog("Sketch ProMax")
            {
                MainInstruction = main, MainContent = content, CommonButtons = TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Cancel, VerificationText = "Don't warn me about this command again",
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Run it anyway");
            var answer = dialog.Show();
            if (dialog.WasVerificationChecked())
            {
                FreezeCause.Forget(id);
                Refresh();
            }
            if (answer == TaskDialogResult.CommandLink1)
            {
                Tools.ActivityLog.Note("heavy_command", $"{warned.Name}: run anyway after the warning");
                return;
            }
            e.Cancel = true;
            Tools.ActivityLog.Note("heavy_command", $"{warned.Name}: cancelled at the warning");
        }
        catch (Exception) { /* never stop Revit's command because of the warning itself */ }
    }
}
