using SketchProMax.Tools;

/// <summary>
/// What the 2026-10-30 review of the chats, tool log and bug log turned into: attachments open in Excel, export settings
/// remembered per model, the join hint, and the chat told the office's recurring practice. No Revit here.
/// </summary>
internal static class HistoryLessonsTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // ---- An attachment another program holds open for writing (Excel with the drawing list open) still reads.
        var temp = Path.Combine(Path.GetTempPath(), "spm-shared-" + Guid.NewGuid().ToString("N") + ".xlsx");
        File.WriteAllBytes(temp, [1, 2, 3, 4]);
        try
        {
            byte[]? read = null;
            string? error = null;
            using (new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                try { read = SharedRead.AllBytes(temp); }
                catch (IOException ex) { error = ex.Message; }
            }
            check("attachments: a file open for writing in another program is read", read is { Length: 4 } && read[3] == 4, error ?? "");
        }
        finally { File.Delete(temp); }

        // ---- Export settings per model
        var all = ExportDefaults.Parse("""{"C:\\M.rvt": {"pdf_setup": "A0 colour", "dwg_setup": "Office", "folder": "C:\\x", "name_pattern": ""}, "bad": 3}""");
        check("export defaults: known keys kept, folder and empty values dropped",
            all.Count == 1 && all["c:\\m.rvt"].Count == 2 && all["C:\\M.rvt"]["pdf_setup"] == "A0 colour" && !all["C:\\M.rvt"].ContainsKey("folder"),
            ExportDefaults.Write(all));
        check("export defaults: unreadable text is no settings", ExportDefaults.Parse("{not json").Count == 0 && ExportDefaults.Parse(null).Count == 0, "");
        var back = ExportDefaults.Parse(ExportDefaults.Write(all));
        check("export defaults: written and read back", back["C:\\M.rvt"]["dwg_setup"] == "Office", ExportDefaults.Write(back));
        var used = new List<string>();
        var mine = all["C:\\M.rvt"];
        var given = ExportDefaults.Pick("pdf_setup", "A3 mono", mine, used);
        var remembered = ExportDefaults.Pick("pdf_setup", null, mine, used);
        var gone = ExportDefaults.Pick("dwg_setup", null, mine, used, _ => false);
        var none = ExportDefaults.Pick("name_pattern", null, mine, used);
        check("export defaults: the given value wins, the remembered one fills in and is said, a deleted setup is skipped",
            given == "A3 mono" && remembered == "A0 colour" && gone is null && none is null && used.SequenceEqual(["pdf_setup 'A0 colour'"]),
            string.Join(" | ", used));

        // ---- Joins written as code: the hint points at the bulk join tool
        check("code hint: a refused join points to join_elements",
            ScriptSource.RuntimeHint("Can't cut joined element.").Contains("join_elements"), ScriptSource.RuntimeHint("Can't cut joined element."));
    }

    /// <summary>Checks on the sources: the prompts carry the practice, and the chat export says Sketch ProMax.</summary>
    public static void RunSources(string root, Action<string, bool, string> check)
    {
        string Read(string path) => File.ReadAllText(Path.Combine(root, path));
        var prompt = Read("src/SketchProMax/Agent/SystemPrompt.cs");
        check("prompt: the office practice goes to every chat and job",
            prompt.Contains("public const string Practice") && prompt.Contains("Base + \"\\n\" + Practice")
            && prompt.Contains("join_elements, not code") && prompt.Contains("never joined to floors") && prompt.Contains("VARIABLE LEVEL"), "");
        var chat = Read("src/SketchProMax/Chat/ClaudeCli.cs");
        check("prompt: rules said with always/never or as a correction are remembered without being asked",
            chat.Contains("Don't make them say it twice") && chat.Contains("\"always\", \"never\", \"typically\""), "");
        check("chat export: replies are headed Sketch ProMax", !Read("src/SketchProMax/Tools/ProjectPackTools.cs").Contains("\"## Claude\"")
            && Read("src/SketchProMax/Tools/ProjectPackTools.cs").Contains("\"## Sketch ProMax\""), "");
    }
}
