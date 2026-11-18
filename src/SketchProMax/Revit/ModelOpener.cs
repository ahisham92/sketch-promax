using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SketchProMax.Revit;

/// <summary>
/// Opens a model from its file for the chat (open_revit_model), also in a Revit window with no model open. A workshared
/// model is always opened detached from central with its worksets kept, so the central model and the user's local file
/// are never touched; nothing is saved. Call from the external event (Revit refuses to open documents in event handlers).
/// </summary>
internal static class ModelOpener
{
    public static (string? Error, string Title, string Text) Open(UIApplication app, string path)
    {
        try
        {
            var open = app.Application.Documents.Cast<Document>().FirstOrDefault(d => !d.IsFamilyDocument && !d.IsLinked
                && !string.IsNullOrEmpty(d.PathName) && string.Equals(Path.GetFullPath(d.PathName), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            if (open is not null)
            {
                if (app.ActiveUIDocument?.Document.Equals(open) != true) app.OpenAndActivateDocument(open.PathName);
                return (null, open.Title, $"'{open.Title}' was open already in Revit {App.RevitYear}; now working on it." +
                                          (open.IsWorkshared && !open.IsDetached ? " It is the user's own working copy, not a detached one: change nothing in it unless the user asked." : ""));
            }

            BasicFileInfo info;
            try { info = BasicFileInfo.Extract(path); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return ($"'{Path.GetFileName(path)}' can't be read as a Revit model.", "", ""); }
            var year = int.TryParse(App.RevitYear, out var y) ? y : 0;
            if (Newer(info.Format, year) is { } saved)
                return ($"'{Path.GetFileName(path)}' was saved in Revit {saved}, which Revit {App.RevitYear} can't open. Open it in a Revit {saved} window (revitVersion).", "", "");

            // A model that was never saved (a detached copy too) can't be made the active one again by Revit's API: once
            // another model is opened over it, only the user's click on its tab brings it back.
            var stranded = app.ActiveUIDocument?.Document is { } before && Bridge.RevitTargets.NeverSaved(before.PathName) ? before.Title : null;
            var options = new OpenOptions();
            if (info.IsWorkshared) options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
            app.DialogBoxShowing += OnDialog;
            UIDocument? ui;
            try { ui = app.OpenAndActivateDocument(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), options, false); }
            finally { app.DialogBoxShowing -= OnDialog; }
            if (ui?.Document is not { } doc) return ($"Revit didn't open '{Path.GetFileName(path)}'.", "", "");
            return (null, doc.Title, Opened(doc.Title, App.RevitYear, info.IsWorkshared, Older(info.Format, year)) +
                                     (stranded is not null ? $" '{stranded}' is still open behind it, but it was never saved, so the tools can't switch back to it: when it is needed again, ask the user to click its tab in Revit." : ""));
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return ($"Revit could not open '{Path.GetFileName(path)}' ({ex.Message}).", "", "");
        }
    }

    /// <summary>What the call answers once the model is open.</summary>
    internal static string Opened(string title, string revit, bool detached, string? upgradedFrom) =>
        $"Opened '{title}' in Revit {revit}; now working on it." +
        (detached ? " It is a detached copy with its worksets kept: the central model is not touched, and the copy is not saved unless the user asks (links that were not found stay unloaded)." : "") +
        (upgradedFrom is not null ? $" It was saved in Revit {upgradedFrom} and was upgraded in memory; saving it would keep the newer format, so don't save it over the original." : "");

    /// <summary>The year a file was saved in when it is newer than this Revit; null when it can be opened or isn't known.</summary>
    internal static string? Newer(string? format, int revitYear) =>
        revitYear > 0 && int.TryParse(format, out var year) && year > revitYear ? format : null;

    /// <summary>The year a file was saved in when it is older than this Revit (it is upgraded on opening); else null.</summary>
    internal static string? Older(string? format, int revitYear) =>
        revitYear > 0 && int.TryParse(format, out var year) && year < revitYear ? format : null;

    /// <summary>
    /// The dialogs Revit shows while it opens a model, answered so the opening doesn't wait for a click with nobody
    /// there: links that can't be found are left unloaded. Every other dialog shows as usual.
    /// </summary>
    private static void OnDialog(object? sender, DialogBoxShowingEventArgs e)
    {
        try
        {
            if (e is TaskDialogShowingEventArgs task && Answer(task.DialogId) is { } answer) e.OverrideResult(answer);
        }
        catch (Exception) { /* a dialog must never fail because of us */ }
    }

    /// <summary>The answer to a dialog of opening a model, by its id; null for one left to the user.</summary>
    internal static int? Answer(string? dialogId) => dialogId switch
    {
        // Unresolved references: "Ignore and continue opening the project" (the second command link).
        "TaskDialog_Unresolved_References" => 1002,
        // A local file whose central can't be reached, opened detached anyway: Close.
        "TaskDialog_Cannot_Find_Central_Model" => 8,
        _ => null,
    };
}
