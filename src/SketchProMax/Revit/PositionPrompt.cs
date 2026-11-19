using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SketchProMax.Revit;

/// <summary>
/// Revit's "Location Position Changed" prompt (Save / Do not save / Disable shared positioning), shown when a model with a
/// linked file whose shared position is marked as changed is saved or synchronized. On 19 Nov 2026 it came up at every
/// auto sync, and the user had to raise it four times ("this is the fourth time"): the chat could only skip auto sync until
/// the next message. Now, only while Sketch ProMax's own sync or save runs (<see cref="During{T}"/>), the prompt is answered
/// "Do not save": every position stays as it is and nothing moves, the choice the user made each time. The user's own
/// Synchronize or Save shows it as usual (Sketch ProMax never answers for the user's commands). What was answered is said
/// in the sync's line, so the chat can tell the user it came up.
/// </summary>
internal static class PositionPrompt
{
    /// <summary>"Do not save" is the prompt's second command link.</summary>
    public const int DoNotSave = 1002;

    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static int _answered;

    public static void Watch(UIApplication app)
    {
        try { app.DialogBoxShowing += OnDialog; } catch (Exception) { }
    }

    public static void Unwatch(UIApplication app)
    {
        try { app.DialogBoxShowing -= OnDialog; } catch (Exception) { }
    }

    /// <summary>
    /// Runs Sketch ProMax's own sync or save (Revit's thread): the prompt is answered "Do not save" while it runs.
    /// <paramref name="answered"/>: how many times it came up.
    /// </summary>
    public static T During<T>(Func<T> work, out int answered)
    {
        if (_depth == 0) _answered = 0;
        _depth++;
        try { return work(); }
        finally
        {
            _depth--;
            answered = _answered;
        }
    }

    /// <summary>The line added to the sync's or save's result when the prompt came up; "" when it didn't.</summary>
    public static string Said(int answered) => answered <= 0 ? "" :
        " Revit asked whether to save a linked file's changed position; Sketch ProMax answered \"Do not save\", so nothing moved. " +
        "The user's own Synchronize still shows that prompt.";

    /// <summary>True for the prompt, by its id or its English text.</summary>
    internal static bool Is(string? dialogId, string? message)
    {
        var id = dialogId ?? "";
        if (id.IndexOf("Location_Position", StringComparison.OrdinalIgnoreCase) >= 0 ||
            id.IndexOf("Position_Changed", StringComparison.OrdinalIgnoreCase) >= 0 ||
            id.IndexOf("SharedPosition", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        var m = message ?? "";
        return m.IndexOf("position", StringComparison.OrdinalIgnoreCase) >= 0 &&
               (m.IndexOf("changed", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("moved", StringComparison.OrdinalIgnoreCase) >= 0) &&
               (m.IndexOf("link", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("shared", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static void OnDialog(object? sender, DialogBoxShowingEventArgs e)
    {
        try
        {
            if (_depth == 0 || e is not TaskDialogShowingEventArgs task || !Is(task.DialogId, task.Message)) return;
            if (!e.OverrideResult(DoNotSave)) return;
            _answered++;
            try { Tools.ActivityLog.Note("position_prompt", "Revit's Location Position Changed prompt during Sketch ProMax's sync or save: answered Do not save."); }
            catch (Exception) { }
        }
        catch (Exception) { /* a dialog must never fail because of us */ }
    }
}
