using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Chat;
using SketchProMax.Tools;

namespace SketchProMax.Revit;

/// <summary>
/// The open model's own names for the home screen's prompt blanks (Chat.PromptSlots): its levels (by elevation), grids,
/// saved sheet sets and, when the berth status is set up for it, its berths. Read in an API call; a part that can't be
/// read is left out, so its blank stays for the user to type.
/// </summary>
internal static class PromptChoices
{
    public static Dictionary<string, List<string>> Read(UIApplication app)
    {
        var choices = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var doc = app.ActiveUIDocument?.Document;
        if (doc is null || doc.IsFamilyDocument) return choices;
        void Add(string slot, Func<List<string>> read)
        {
            try { if (read() is { Count: > 0 } names) choices[slot] = names; }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException or IOException) { }
        }
        Add("level", () => PromptSlots.Tidy(new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => l.Elevation).Select(l => l.Name), keepOrder: true));
        Add("grid", () => PromptSlots.Tidy(new FilteredElementCollector(doc).OfClass(typeof(Grid)).Select(g => g.Name)));
        Add("sheet_set", () => PromptSlots.Tidy(new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Select(s => s.Name)));
        Add("berth", () =>
        {
            var file = string.IsNullOrEmpty(doc.PathName) ? doc.Title : Path.GetFileName(doc.PathName);
            if (BerthStatusMath.LoadSetup(ChecklistProjects.ProjectKey(file)) is not { } setup) return [];
            var berths = BerthStatusMath.ModelFor(setup, file) is { } model ? BerthStatusMath.BerthsOf(setup, model) : setup.Berths;
            return PromptSlots.Tidy(berths.Select(b => b.Name));
        });
        return choices;
    }
}
