namespace SketchProMax.UI;

/// <summary>
/// The ribbon buttons, pane buttons and pane tabs this core declares, sent to the loader as Query("ribbon"). A loader
/// from 0.30 on adds them while Revit runs, right after an in-session update (see CoreRibbon in the loader), so a new
/// button needs no new loader. Older loaders never ask; the core then says a restart is needed (<see cref="LoaderCompat"/>).
///
/// To add a button that also appears after a live update:
///   1. add an entry below: Button(panel, id, text, command, glyph, tooltip, long description, needsModel, small, live);
///      panels appear in the order they are first named (a new one goes at the end of the tab); id is unique and never
///      reused; glyph is one of the loader's icon names (unknown names get the plain cube), or "logo" / "logo:green";
///   2. handle "command" in CoreEntry.Command.
/// Pulldown(panel, id, text, glyph, tooltip) and Split(...) make drop-downs; Entry(dropDownId, id, text, command, ...)
/// adds their items. small: true stacks consecutive small controls two or three high. live: "key" makes the text,
/// tooltip, icon and state follow CoreEntry.Query("live|key") ("text \t tooltip \t glyph \t state", see LiveLine).
/// Pane(section, id, text, command, tooltip) adds a button to the pane's Activity view; Tab(name, header, accessible
/// name) adds a pane tab whose content CoreEntry.PaneContent(name) returns. Hide(id) hides a loader-built button.
/// A button the loader already builds itself (same id) is left as it is. Moving an item to another panel is fine:
/// the loader hides the old one and makes a new one, live. See RibbonManifest in the loader for the line format.
/// </summary>
internal static class CoreRibbonManifest
{
    private static readonly string[] Lines =
    [
        // Bug reports (0.30): handled by CoreEntry.Command "report-bug" / "bug-folder" / "export-chats-bugs"; the notes tab by PaneContent("notes").
        Button("Sketch ProMax", "ReportBug", "Report\nbug", "report-bug", "bug",
            "Writes a bug report for Sketch ProMax with what just happened, ready to send."),
        Button("Sketch ProMax", "BugFolder", "Bug\nfolder", "bug-folder", "folder",
            "Opens the folder with the bug reports written so far."),
        Button("Sketch ProMax", "ExportChatsBugs", "Export chats\n& bugs", "export-chats-bugs", "pack",
            "Writes one zip with the chats of every model, all bug reports, the tool-call log and the versions, to send to the developer."),
        Pane("Bugs", "ReportBug", "Report bug", "report-bug", "Writes a bug report for Sketch ProMax with what just happened, ready to send."),
        Pane("Bugs", "BugFolder", "Open bug folder", "bug-folder", "Opens the folder with the bug reports written so far."),
        Pane("Bugs", "ExportChatsBugs", "Export chats & bugs", "export-chats-bugs",
            "Writes one zip with the chats of every model, all bug reports, the tool-call log and the versions, to send to the developer. Asks for the period first; nothing is sent."),
        Tab("notes", "Notes", "Notes for this model"),
    ];

    public static string Text => string.Join("\n", Lines);

    internal static string Button(string panel, string id, string text, string command, string glyph, string tip, string more = "",
        bool needsModel = false, bool small = false, string? live = null) =>
        Join("button", panel, id, text, command, glyph, tip, more, Flags(needsModel, small, live));

    internal static string Pulldown(string panel, string id, string text, string glyph, string tip, string more = "", bool small = false, string? live = null) =>
        Join("pulldown", panel, id, text, "", glyph, tip, more, Flags(false, small, live));

    internal static string Split(string panel, string id, string text, string glyph, string tip, string more = "", bool small = false) =>
        Join("split", panel, id, text, "", glyph, tip, more, Flags(false, small, null));

    internal static string Entry(string dropDown, string id, string text, string command, string glyph, string tip, string more = "", bool needsModel = false) =>
        Join("item", dropDown, id, text, command, glyph, tip, more, Flags(needsModel, false, null));

    internal static string Hide(string id) => Join("hide", "", id);

    private static string Flags(bool needsModel, bool small, string? live) =>
        string.Join(",", new[] { needsModel ? "model" : "", small ? "small" : "", live is null ? "" : "live:" + live }.Where(f => f.Length > 0));

    /// <summary>A live label's answer: text, tooltip, glyph and "disabled" when it can't be clicked now.</summary>
    internal static string LiveLine(string text, string tip, string glyph, bool enabled = true) =>
        string.Join("\t", Escape(text), Escape(tip), Escape(glyph), enabled ? "" : "disabled");

    internal static string Pane(string section, string id, string text, string command, string tip) =>
        Join("pane", section, id, text, command, "", tip, "", "");

    internal static string Tab(string name, string header, string accessibleName) =>
        Join("tab", "", name, header, "", "", accessibleName, "", "");

    internal static string Join(params string[] fields) => string.Join("\t", fields.Select(Escape));

    /// <summary>Line breaks become "\n" and backslashes "\\" (the loader turns them back); tabs become spaces.</summary>
    internal static string Escape(string? value) =>
        (value ?? "").Replace("\\", "\\\\").Replace("\r", "").Replace("\n", "\\n").Replace("\t", " ");
}
