using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SketchProMax.Revit;

/// <summary>
/// A hint before Revit commands that have frozen Revit (FreezeCause.Load: Run systems analysis from the start, and
/// any command the user ticked in the Rescue window after it froze Revit; never sync, save, open, close or links). Each
/// is bound with an AddInCommandBinding whose BeforeExecuted shows a hint in the corner (UI.CommandHint) and lets Revit
/// run the command at once (no Executed handler, so Revit's own command runs): nothing is ever cancelled. "Don't show
/// this again" takes it off the list. A command another add-in has bound already is left alone.
/// A binding is never removed while Revit runs: removing (and binding again) a command that is running, such as Create
/// Spot Elevation waiting for a click while a newer core was switched to, closed Revit on 27 Oct 2026. A command taken
/// off the list keeps its binding and runs without a word; a newer core takes the bindings over from the older one.
/// </summary>
internal static class HeavyCommands
{
    // Kept in the AppDomain so that the next core switched to while Revit runs finds the bindings already made.
    private const string Slot = "SketchProMax.HeavyCommands.Bound";
    private static Dictionary<string, AddInCommandBinding> Bound = new(StringComparer.Ordinal);
    private static UIApplication? _app;

    /// <summary>The commands bound now (for the field test).</summary>
    internal static IReadOnlyCollection<string> BoundIds => Bound.Keys;

    /// <summary>Takes over the bindings of an older core and binds the commands on the list. A valid Revit API context.</summary>
    public static void Start(UIApplication app)
    {
        _app = app;
        if (AppDomain.CurrentDomain.GetData(Slot) is Dictionary<string, AddInCommandBinding> earlier) Bound = earlier;
        else AppDomain.CurrentDomain.SetData(Slot, Bound);
        foreach (var binding in Bound.Values)
            try { binding.BeforeExecuted += OnBefore; } catch (Exception) { }
        Apply(app);
    }

    /// <summary>The list changed (a command learned in the Rescue window): bind at Revit's next idle moment. Any thread.</summary>
    public static void Refresh()
    {
        try { _ = App.Dispatcher?.InvokeAsync(app => { Apply(app); return true; }); } catch (Exception) { }
    }

    /// <summary>This core stops: its handler goes, the bindings stay for the next core (or for Revit to end).</summary>
    public static void Stop()
    {
        foreach (var binding in Bound.Values)
            try { binding.BeforeExecuted -= OnBefore; } catch (Exception) { }
        _app = null;
    }

    private static void Apply(UIApplication app)
    {
        if (_app is null) return;
        foreach (var id in FreezeCause.Load().Select(w => w.Id))
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
    }

    /// <summary>
    /// Before a command that froze Revit before: a hint in the corner, and the command runs at once. Never a question and
    /// never a cancel (0.35.52's warning had Cancel as its default and stopped the user's syncs and links).
    /// </summary>
    private static void OnBefore(object? sender, BeforeExecutedEventArgs e)
    {
        try
        {
            var id = e.CommandId?.Name ?? "";
            // Off the list ("Don't show this again"), or one of the everyday long commands: nothing shown.
            if (FreezeCause.Load().FirstOrDefault(w => w.Id == id) is not { } warned) return;
            var (main, content) = FreezeCause.Warning(warned);
            UI.CommandHint.Show(main, content, () => FreezeCause.Forget(id));
            Tools.ActivityLog.Note("heavy_command", $"{warned.Name}: hint shown, the command ran");
        }
        catch (Exception) { /* never stop Revit's command because of the hint itself */ }
    }
}
