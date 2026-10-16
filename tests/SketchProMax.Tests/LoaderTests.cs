using SketchProMax.Loader;
using SketchProMax.Revit;
using SketchProMax.UI;

/// <summary>The core's ribbon manifest as the loader reads it, the old-loader check, and the "Revit didn't take it" wording.</summary>
internal static class LoaderTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // A core's lines read back by the loader, line breaks and backslashes intact.
        var text = string.Join("\n",
            CoreRibbonManifest.Button("Checks", "MyCheck", "My\ncheck", "my-check", "check", "Tip with a \\ backslash", "More", needsModel: true),
            CoreRibbonManifest.Pane("Bugs", "Report", "Report bug", "report-bug", "Writes a report"),
            CoreRibbonManifest.Tab("notes", "Notes", "Notes for this model"),
            "hide\t\tOldButton",
            "gallery\tChecks\tFuture\tFuture thing",
            "button\tChecks\tMyCheck\tDuplicate\tother",
            "# a comment",
            "");
        var m = RibbonManifest.Parse(text);
        var b = m.Controls.Count == 1 ? m.Controls[0] : null;
        check("manifest: button read back", b is { Group: "Checks", Id: "MyCheck", Text: "My\ncheck", Command: "my-check", Glyph: "check", Tip: "Tip with a \\ backslash", More: "More", NeedsModel: true }, b?.ToString() ?? "none");
        check("manifest: pane button, tab, hide", m.PaneActions.Count == 1 && m.PaneActions[0].Command == "report-bug"
            && m.Tabs.Count == 1 && m.Tabs[0].Id == "notes" && m.Tabs[0].Text == "Notes" && m.Hide.SequenceEqual(["OldButton"]), "");
        check("manifest: unknown kinds are kept for the restart note", m.Unknown.SequenceEqual(["Future thing"]), string.Join("|", m.Unknown));
        check("manifest: empty from an older core", RibbonManifest.Parse("").Controls.Count == 0 && RibbonManifest.Parse(null).Unknown.Count == 0, "");
        var shipped = RibbonManifest.Parse(CoreRibbonManifest.Text);
        check("manifest: the core's own manifest parses cleanly", shipped.Unknown.Count == 0
            && shipped.Controls.All(x => x.Id.Length > 0 && (x.IsDropDown || x.Command.Length > 0)), string.Join("|", shipped.Unknown));

        // The full layout: drop-downs with entries, stacked small buttons, live labels, entries before their drop-down.
        var layout = RibbonManifest.Parse(string.Join("\n",
            CoreRibbonManifest.Entry("Structure", "Columns", "Columns", "place-columns", "column", "Places columns", needsModel: true),
            CoreRibbonManifest.Pulldown("Model", "Structure", "Structure", "cube", "Structural tools"),
            CoreRibbonManifest.Split("Model", "Steel", "Steel", "cube", "Steel tools"),
            CoreRibbonManifest.Entry("Steel", "Members", "Members", "steel-members", "cube", "Steel members"),
            CoreRibbonManifest.Entry("Nowhere", "Orphan", "Orphan", "x", "", "No drop-down"),
            CoreRibbonManifest.Button("Sketch ProMax", "Job", "No job", "job", "job", "The running job", small: true, live: "job"),
            CoreRibbonManifest.Button("Sketch ProMax", "Update", "Version", "update-info", "version", "Updates", small: true, live: "update"),
            CoreRibbonManifest.Button("Sketch ProMax", "Report", "Report bug", "report-bug", "bug", "Report a bug", small: true),
            CoreRibbonManifest.Button("Sketch ProMax", "Settings", "Settings", "settings", "settings", "Settings", small: true),
            CoreRibbonManifest.Button("Sketch ProMax", "Help", "Help", "help", "logo", "Help"),
            CoreRibbonManifest.Hide("Version")));
        var units = RibbonManifest.Units(layout.Controls.Where(c => c.Group == "Sketch ProMax"));
        check("layout: drop-downs and their entries", layout.Controls.Count(c => c.IsDropDown) == 2
            && layout.Entries["Structure"].Single().Command == "place-columns" && layout.Entries["Structure"][0].NeedsModel
            && layout.Entries["Steel"].Single().Id == "Members" && layout.Unknown.SequenceEqual(["Orphan"]), string.Join("|", layout.Unknown));
        check("layout: small buttons stack three high, then a new stack", units.Select(u => u.Count).SequenceEqual([3, 1, 1])
            && units[0][0].LiveKey == "job" && units[2][0].LiveKey is null && layout.Hide.SequenceEqual(["Version"]),
            string.Join(",", units.Select(u => u.Count)));
        var live = RibbonManifest.Live(CoreRibbonManifest.LiveLine("Running\n2 min", "Tip", "job-running", enabled: false));
        check("layout: live label answer read back", live == ("Running\n2 min", "Tip", "job-running", false)
            && RibbonManifest.Live("") == (null, null, null, true), live.ToString());
        check("manifest: restart note names the missing items", RibbonManifest.RestartNote(["My\ncheck"]).Contains("(My check)")
            && RibbonManifest.RestartNote([]).StartsWith("Restart Revit", StringComparison.Ordinal), "");

        // Loaders before 0.30 publish no release: the core then asks for a restart.
        check("loader compat: no release is old", LoaderCompat.RibbonOutdated(null) && LoaderCompat.RibbonOutdated(""), "");
        check("loader compat: 0.29 is old, 0.30 and later are not", LoaderCompat.RibbonOutdated("0.29.0")
            && !LoaderCompat.RibbonOutdated(LoaderCompat.RibbonChangedIn.ToString(3)) && !LoaderCompat.RibbonOutdated("9.0.0"), "");

        // Why Revit didn't take a request.
        var dialog = RevitBlock.Explain(new RevitBlock.State(RevitBlock.Kind.Dialog, " Save changes? "), "export_pdf (job 3)", 4);
        check("blocked: names the dialog", dialog.Contains("'Save changes?'") && dialog.StartsWith("export_pdf (job 3) could not start", StringComparison.Ordinal)
            && dialog.Contains("nothing was changed"), dialog);
        check("blocked: untitled dialog, busy Revit, edit mode", RevitBlock.Explain(new(RevitBlock.Kind.Dialog, null), "x", 4).Contains("dialog or message box")
            && RevitBlock.Explain(new(RevitBlock.Kind.NotResponding, null), "x", 20).Contains("isn't responding")
            && RevitBlock.Explain(new(RevitBlock.Kind.None, null), "x", 20).Contains("edit mode"), "");
    }
}
