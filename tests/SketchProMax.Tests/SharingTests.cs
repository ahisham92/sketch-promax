using SketchProMax.UI;
using static SketchProMax.Sharing.SharedPackMath;

/// <summary>Shared tool packs (Share tools and TritoRevit): the pack format, the ribbon lines, updates, what is copied in, and the files that ship.</summary>
internal static class SharingTests
{
    public static void Run(string root, Action<string, bool, string> check)
    {
        // ---- Ids and paths
        check("sharing: slug from a name", Slug("QA tools") == "qa-tools" && Slug("  Pre-submission  & cleanup!! ") == "pre-submission-cleanup", Slug("QA tools"));
        check("sharing: slug of nothing usable", Slug("!!!") == "shared-tools" && Slug("") == "shared-tools", "");
        check("sharing: slug is a valid id", ValidId(Slug(new string('x', 90))) && Slug(new string('x', 90)).Length <= 40, "");
        check("sharing: ids", ValidId("qa-tools") && !ValidId("QA") && !ValidId("-a") && !ValidId("a/b") && !ValidId(null), "");
        check("sharing: tool ids", ValidToolId("cmd.tool:check_model") && ValidToolId("recipe.my level setup") && !ValidToolId("cmd.") && !ValidToolId("x.y") && !ValidToolId("learned.a\tb"), "");
        check("sharing: safe relative paths", SafeRelative("learned/x.json") && SafeRelative("recipes\\a b.json")
            && !SafeRelative("../x.json") && !SafeRelative("/etc/x") && !SafeRelative("C:\\x") && !SafeRelative("a//b") && !SafeRelative("a/./b") && !SafeRelative(""), "");

        // ---- What a pack is made of
        (string, string, string)? Ribbon(string name) => name == "check_model" ? ("Model check", "check", "Checks the model.") : name == "redline_apply" ? ("Redlines", "pen", "AI") : null;
        string? Learned(string name) => name == "tidy_tags" ? "Tidies tags." : null;
        string? Recipe(string name) => name == "level setup" ? "Makes levels." : null;
        var tools = ToolsFor(["command:pre-submission", "tool:check_model", "tool:redline_apply", "tool:nope", "learned:tidy_tags", "learned:gone",
            "recipe:level setup", "command:settings", "bogus", "tool:check_model"], Ribbon, Learned, Recipe);
        check("sharing: tools in order, AI tools, unknown items and repeats dropped",
            tools.Select(t => t.Id).SequenceEqual(["cmd.pre-submission", "cmd.tool:check_model", "learned.tidy_tags", "recipe.level setup"]),
            string.Join(",", tools.Select(t => t.Id)));
        check("sharing: learned tools and recipes carry their file", tools[2].File == "learned/tidy_tags.json" && tools[3].File == "recipes/level setup.json" && tools[2].Title == "Tidy tags", tools[3].File ?? "");
        check("sharing: what the buttons run", CommandFor(tools[0]) == "pre-submission" && CommandFor(tools[1]) == "tool:check_model"
            && CommandFor(tools[2]) == "quick\tlearned\ttidy_tags" && CommandFor(tools[3]) == "quick\trecipe\tlevel setup"
            && CommandFor(new Tool { Kind = "command", Name = "a\tb" }) is null && CommandFor(new Tool { Kind = "other", Name = "x" }) is null, "");
        check("sharing: shareable commands", Shareable("pre-submission") && Shareable("tool:check_model") && !Shareable("tool:redline_apply")
            && !Shareable("tool:") && !Shareable("chat") && !Shareable("settings") && !Shareable("update"), "");
        check("sharing: the PDF editor can be shared", Shareable("pdf-editor") && CommandFor(new Tool { Kind = "command", Name = "pdf-editor" }) == "pdf-editor"
            && Commands.Any(c => c.Command == "pdf-editor" && c.Glyph == "pdf"), "");
        check("sharing: the pre-submission checklist is offered first", Commands[0].Command == "pre-submission", Commands[0].Command);

        // ---- pack.json
        var pack = new Pack { Id = "qa-tools", Name = "QA tools", Revision = 3, UpdateFrom = @"\\server\share\Tools", MinVersion = "0.34.7", Note = "Line one", Tools = tools, Checklist = ChecklistFile };
        var back = Parse(Serialize(pack), out var why);
        check("sharing: pack round trip", back is not null && back.Id == "qa-tools" && back.Revision == 3 && back.UpdateFrom == pack.UpdateFrom
            && back.Tools.Count == 4 && back.Tools[2].File == "learned/tidy_tags.json" && back.Checklist == ChecklistFile, why);
        check("sharing: a pack from a newer version is not read", Parse("""{"format": 99, "id": "a", "tools": []}""", out why) is null && why.Contains("newer"), why);
        check("sharing: a pack with a bad id is not read", Parse("""{"format": 1, "id": "../x"}""", out why) is null, why);
        check("sharing: damaged or empty pack files are not read", Parse("{ nope", out _) is null && Parse("", out _) is null && Parse(null, out _) is null, "");
        var hostile = Parse("""
            {"format": 1, "id": "a", "checklist": "../../x.json", "tools": [
              {"id": "learned.x", "kind": "learned", "name": "x", "file": "..\\..\\evil.json"},
              {"id": "learned.x", "kind": "learned", "name": "x2"},
              {"id": "cmd.a", "kind": "script", "name": "a"},
              {"id": "nope", "kind": "command", "name": "a"},
              null]}
            """, out why);
        check("sharing: unsafe files, repeats and unknown kinds stripped", hostile is not null && hostile.Tools.Count == 1 && hostile.Tools[0].File is null
            && hostile.Tools[0].Name == "x" && hostile.Checklist is null, why);

        // ---- buttons.txt, written by the core and read by TritoRevit
        var tricky = new Pack { Id = "p", Name = "Tabs\tand\nlines \\ here", Revision = 7, Tools =
        [
            new Tool { Id = "cmd.tool:check_model", Kind = "command", Title = "Model check", Glyph = "check", Tip = "Line 1\nLine 2 \\n literal", NeedsModel = true },
            new Tool { Id = "learned.x", Kind = "learned", Title = "A very long title that will not fit on a ribbon button", Tip = "", NeedsModel = false },
        ] };
        var text = ButtonsText(tricky);
        var read = TritoRevit.PackButtons.Parse(text);
        check("sharing: buttons file round trip", read is not null && read.Id == "p" && read.Revision == 7 && read.Name == "Tabs and\nlines \\ here"
            && read.Buttons.Count == 2 && read.Buttons[0].Tip == "Line 1\nLine 2 \\n literal" && read.Buttons[0].NeedsModel && !read.Buttons[1].NeedsModel,
            read is null ? "null" : read.Name + " | " + read.Buttons.FirstOrDefault()?.Tip);
        check("sharing: button labels fit, default glyphs", read is not null && read.Buttons[1].Text.Length <= 32 && read.Buttons[1].Text.EndsWith("…") && read.Buttons[1].Glyph == "learned", read?.Buttons[1].Text ?? "");
        check("sharing: header matches TritoRevit", ButtonsHeader.StartsWith(TritoRevit.PackButtons.Header + "\t"), ButtonsHeader);
        check("sharing: not a buttons file", TritoRevit.PackButtons.Parse("hello\npack\ta") is null && TritoRevit.PackButtons.Parse("") is null
            && TritoRevit.PackButtons.Parse(ButtonsHeader + "\nbutton\tx\tX") is null, "");
        var later = TritoRevit.PackButtons.Parse(ButtonsHeader + "\npack\tq\tQ\t2\nfuture\tthing\nbutton\ta\tA\nbutton\ta\tAgain\nbutton\t\tNo id\n");
        check("sharing: unknown lines, repeats and blanks skipped", later is not null && later.Buttons.Count == 1 && later.Buttons[0].Text == "A" && later.Buttons[0].Glyph == "cube", "");

        // Installed packs on disk: only folders named as their pack, none being swapped.
        var dir = Path.Combine(Path.GetTempPath(), "spm-share-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Put(string folder, string id)
            {
                Directory.CreateDirectory(Path.Combine(dir, folder));
                File.WriteAllText(Path.Combine(dir, folder, ButtonsFile), ButtonsText(new Pack { Id = id, Name = id.ToUpperInvariant(), Revision = 1 }));
            }
            Put("b-pack", "b-pack"); Put("a-pack", "a-pack"); Put("c-pack.incoming", "c-pack"); Put("wrong", "other");
            var packs = TritoRevit.PackButtons.ReadAll(dir);
            check("sharing: installed packs read, swaps and misnamed skipped", packs.Select(p => p.Id).SequenceEqual(["a-pack", "b-pack"]), string.Join(",", packs.Select(p => p.Id)));
            var before = TritoRevit.PackButtons.Signature(dir);
            Put("d-pack", "d-pack");
            check("sharing: the signature changes when a pack arrives", before != TritoRevit.PackButtons.Signature(dir), before);
            check("sharing: no packs folder", TritoRevit.PackButtons.ReadAll(Path.Combine(dir, "none")).Count == 0 && TritoRevit.PackButtons.Signature(Path.Combine(dir, "none")) == "-", "");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        // ---- Updates
        check("sharing: newer revision", IsNewer(new Pack { Id = "a", Revision = 2 }, new Pack { Id = "a", Revision = 1 })
            && !IsNewer(new Pack { Id = "a", Revision = 1 }, new Pack { Id = "a", Revision = 1 })
            && !IsNewer(new Pack { Id = "b", Revision = 9 }, new Pack { Id = "a", Revision = 1 })
            && IsNewer(new Pack { Id = "a", Revision = 1 }, null) && !IsNewer(null, null), "");
        check("sharing: next revision is past both", NextRevision(0, 0) == 1 && NextRevision(3, 1) == 4 && NextRevision(2, 5) == 6 && NextRevision(-4, -1) == 1, "");
        var files = new Dictionary<string, string> { ["learned/a.json"] = "{}", ["recipes/b.json"] = "[]" };
        var print = Fingerprint(pack, files);
        var stamped = new Pack { Id = pack.Id, Name = pack.Name, Note = pack.Note, UpdateFrom = pack.UpdateFrom, Tools = pack.Tools, Revision = 99, Published = DateTime.UtcNow };
        check("sharing: fingerprint ignores revision and date", print == Fingerprint(stamped, files.Reverse()), "");
        check("sharing: fingerprint follows a changed file or note", print != Fingerprint(pack, new Dictionary<string, string> { ["learned/a.json"] = "{ }", ["recipes/b.json"] = "[]" })
            && print != Fingerprint(new Pack { Id = pack.Id, Name = pack.Name, Note = "changed", UpdateFrom = pack.UpdateFrom, Tools = pack.Tools }, files), "");
        var record = ParseRecord(SerializeRecord(new Record { Id = "qa-tools", Name = "QA tools", Revision = 2, Items = ["command:pre-submission"], Fingerprint = print }));
        check("sharing: publish record round trip", record is not null && record.Revision == 2 && record.AutoPublish && record.Items.Single() == "command:pre-submission"
            && ParseRecord("""{"id": "Bad Id"}""") is null && ParseRecord("nope") is null, "");

        // ---- What is copied in on the recipient's PC
        var mine = new Owned("qa-tools", "h1");
        check("sharing: new file copied", Decide("qa-tools", "h2", null, null).Step == Step.Copy, "");
        check("sharing: same file left", Decide("qa-tools", "h2", "h2", null).Step == Step.Same, "");
        check("sharing: a user's own tool of that name kept", Decide("qa-tools", "h2", "h9", null).Step == Step.Keep, "");
        check("sharing: another pack's tool kept", Decide("qa-tools", "h2", "h1", new Owned("other", "h1")).Step == Step.Keep, "");
        check("sharing: a pack's tool edited here kept", Decide("qa-tools", "h2", "h9", mine).Step == Step.Keep, "");
        check("sharing: a pack's untouched tool updated", Decide("qa-tools", "h2", "h1", mine).Step == Step.Copy, "");
        check("sharing: checklist with none saved", DecideChecklist("qa-tools", false, null, null).Write, "");
        check("sharing: checklist from this pack replaced", DecideChecklist("qa-tools", true, ChecklistSource("qa-tools"), null).Write, "");
        check("sharing: the PC's own checklist kept", !DecideChecklist("qa-tools", true, @"C:\x.xlsx", null).Write && !DecideChecklist("qa-tools", true, ChecklistSource("other"), null).Write
            && !DecideChecklist("qa-tools", true, "edited", null).Write, "");
        check("sharing: a checklist Excel in Settings wins", !DecideChecklist("qa-tools", false, null, @"C:\x.xlsx").Write, "");
        check("sharing: the sharer's checklist goes only when ticked", ParseRecord(SerializeRecord(new Record { Id = "a" }))?.IncludeChecklist == false
            && ParseRecord(SerializeRecord(new Record { Id = "a", IncludeChecklist = true }))?.IncludeChecklist == true, "");

        // ---- The zip's words
        check("sharing: zip name", ZipName("QA tools") == "TritoRevit - QA tools.zip" && ZipName("a/b:c") == "TritoRevit - a_b_c.zip" && ZipName(" ") == "TritoRevit - Shared tools.zip", ZipName("a/b:c"));
        var readme = Readme(pack);
        check("sharing: readme says how to install and update", readme.Contains("Install TritoRevit.cmd") && readme.Contains("first time only") && readme.Contains("Model check")
            && readme.Contains(pack.UpdateFrom) && readme.Contains("no restart"), readme);
        check("sharing: readme gives the one-line install for PCs that block .cmd files", readme.Contains("cannot access the specified device") && readme.Contains(InstallLine), readme);
        check("sharing: readme without an update folder", Readme(new Pack { Id = "a", Name = "A" }).Contains("install a newer zip"), "");

        // ---- Sketch ProMax's ribbon (0.35.14, the owner asked for it to be clear): Share tools is a large button in the first panel, after Lifeline
        var shipped = SketchProMax.Loader.RibbonManifest.Parse(CoreRibbonManifest.Text).Controls.Where(c => c.Group == "Claude").ToList();
        var at = shipped.FindIndex(c => c.Command == "share-tools");
        check("sharing: Share tools is large in the first panel, after Lifeline", at > 0 && shipped[at - 1].Command == "recover" && !shipped[at].Small && !shipped[at - 1].Small,
            string.Join(",", shipped.Select(c => c.Id)));

        // ---- Files that ship
        // ---- Only the owner's PC shares tools (Share tools and the automatic next revision)
        var sharedTools = Src("Sharing", "SharedTools.cs");
        check("sharing: only the owner's PC can share tools or publish a pack's next revision",
            sharedTools.Contains("if (!OwnerPc())") && sharedTools.Contains("if (App.Shared || !OwnerPc()) return;")
            && sharedTools.Contains("LicenseState.IsOwnerPc"), "");

        // ---- Help > Open folders: the same folders the add-in writes to
        var folders = SketchProMax.Bugs.UserFolders.List();
        string Src(params string[] parts) => Read(Path.Combine([root, "src", "SketchProMax", .. parts]));
        check("folders: every folder has a title, a path and a tip, none twice", folders.Count >= 8
            && folders.All(f => f.Title.Length > 0 && f.Path.Length > 0 && f.Tip.Length > 0)
            && folders.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == folders.Count, string.Join(" | ", folders.Select(f => f.Path)));
        bool Ends(string title, params string[] tail) => folders.Any(f => f.Title == title && f.Path.EndsWith(Path.Combine(tail), StringComparison.Ordinal));
        check("folders: the paths match the add-in's own", Ends("Bugs", "Sketch ProMax", "Bugs") && Ends("Learned tools", "SketchProMax", "learned-tools")
            && Ends("Recipes", "SketchProMax", "recipes") && Ends("Chats", "SketchProMax", "chat", "conversations")
            && Ends("PDF backups", "Sketch ProMax", "PDF backups") && Ends("Field tests", "SketchProMax", "field-tests") && Ends("Checklists", "Sketch ProMax", "Checklists")
            && Src("Tools", "LearnedTools.cs").Contains("\"SketchProMax\", \"learned-tools\"") && Src("Tools", "RecipeTools.cs").Contains("\"SketchProMax\", \"recipes\"")
            && Src("Chat", "ChatStore.cs").Contains("\"SketchProMax\", \"chat\", \"conversations\"") && Src("Tools", "FieldTest.cs").Contains("\"SketchProMax\", \"field-tests\"")
            && Src("UI", "PdfEditorWindow.cs").Contains("\"Sketch ProMax\", \"PDF backups\"") && Src("Sharing", "UserFolder.cs").Contains("\"Sketch ProMax\", \"Checklists\""), "");
        check("folders: Help > Open folders runs the folders window", CoreRibbonManifest.Text.Contains("\tuser-folders\t")
            && Src("CoreEntry.cs").Contains("\"user-folders\" => UserFoldersWindow.Open()"), "");

        var trito = Path.Combine(root, "src", "TritoRevit");
        var addin = Read(Path.Combine(trito, "TritoRevit.addin"));
        check("sharing: TritoRevit manifest", addin.Contains(@"<Assembly>TritoRevit\TritoRevit.dll</Assembly>") && addin.Contains("<FullClassName>TritoRevit.TritoApp</FullClassName>")
            && !addin.Contains("SketchProMax.Loader"), "");
        var proj = Read(Path.Combine(trito, "TritoRevit.csproj"));
        check("sharing: TritoRevit.dll keeps one assembly version", proj.Contains("<AssemblyVersion>1.0.0.0</AssemblyVersion>") && proj.Contains("<Deterministic>true</Deterministic>"), "");
        var slots = Read(Path.Combine(trito, "Slots.cs"));
        var slotCount = System.Text.RegularExpressions.Regex.Matches(slots, @"class Slot\d\d\b").Count;
        var slotConst = System.Text.RegularExpressions.Regex.Match(Read(Path.Combine(trito, "TritoApp.cs")), @"Slots\s*=\s*(\d+)");
        check("sharing: one command class per button slot", slotCount == 96 && (!slotConst.Success || slotConst.Groups[1].Value == "96"), $"{slotCount} {slotConst.Value}");
        var app = Read(Path.Combine(trito, "TritoApp.cs"));
        check("sharing: TritoRevit's Update button checks in place", app.Contains("\"TritoRevit.Update\"") && app.Contains("\"update\") command = \"shared-update\"")
            && Read(Path.Combine(root, "src", "SketchProMax", "CoreEntry.cs")).Contains("\"shared-update\" => Sharing.SharedTools.CheckForUpdates(app)"), "");
        var shared = Read(Path.Combine(root, "src", "SketchProMax", "Sharing", "SharedTools.cs"));
        var check4 = shared.Substring(Math.Max(0, shared.IndexOf("CheckForUpdates(UIApplication", StringComparison.Ordinal)));
        check("sharing: the Update check never opens a folder", shared.Contains("UpdateStage.UpToDate") && !check4.Substring(0, Math.Min(check4.Length, 2500)).Contains("explorer", StringComparison.OrdinalIgnoreCase), "");
        var ps1 = Read(Path.Combine(root, "src", "SketchProMax", "Sharing", "install-tritorevit.ps1"));
        check("sharing: installer downloads the add-in only when missing and keeps a newer pack",
            ps1.Contains("$needs") && ps1.Contains("Get-LatestRelease") && ps1.Contains("Copy-Loader") && ps1.Contains("FileVersion") && ps1.Contains(".incoming") && ps1.Contains("revision"), "");
        check("sharing: installer never touches Sketch ProMax's own manifest", ps1.Contains("SketchProMax.addin"), "");
        var cmd = Read(Path.Combine(root, "src", "SketchProMax", "Sharing", "install-tritorevit.cmd"));
        check("sharing: the install command needs no admin", cmd.Contains("-ExecutionPolicy Bypass") && !cmd.Contains("runas", StringComparison.OrdinalIgnoreCase), "");
        check("sharing: the .cmd reads the installer as text (no -File, so signed-only policies don't stop it)",
            cmd.Contains("iex (Get-Content -Raw") && !cmd.Contains("-File") && cmd.Contains("cd /d \"%~dp0\""), cmd);
        check("sharing: the installer also runs pasted, from the current folder",
            ps1.Contains("if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).ProviderPath }") && ps1.Contains("LanguageMode"), "");
        var build = Read(Path.Combine(root, "build.ps1"));
        check("sharing: build puts TritoRevit in every version", build.Contains("TritoRevit.csproj") && build.Contains("TritoRevit.addin"), "");
        var release = Read(Path.Combine(root, ".github", "workflows", "release.yml"));
        check("sharing: release checks TritoRevit is in the zip", release.Contains("TritoRevit.dll"), "");

        // ---- The TritoRevit folder in Documents (problem reports and checklists)
        var listPack = new Pack { Id = "qa", Name = "QA tools", UpdateFrom = @"\\server\share" };
        var checklistPack = new Pack { Id = "psc", Name = "Pre Submission Checklist", Tools = [new Tool { Id = "cmd.pre-submission", Kind = "command", Name = "pre-submission" }] };
        check("sharing: folder named after the pack with the checklist", FolderName([listPack, checklistPack]) == "TritoRevit - Pre Submission Checklist", FolderName([listPack, checklistPack]));
        check("sharing: folder named after the only pack, or Shared tools", FolderName([listPack]) == "TritoRevit - QA tools" && FolderName([]) == "TritoRevit - Shared tools", "");
        check("sharing: pack names made safe for a folder", CleanName("A/B: C?*  D.") == "A B C D" && CleanName(new string('x', 90)).Length == 60 && FolderName([new Pack { Name = "???" }]) == "TritoRevit - Shared tools", CleanName("A/B: C?*  D."));
        var readMe = ReadMe([listPack]);
        check("sharing: the how-to says where reports go", readMe.Contains("Bugs") && readMe.Contains("Report a problem") && readMe.Contains(@"\\server\share") && !readMe.Contains("Claude"), readMe);
        check("sharing: the how-to without an update folder says to send the zips", ReadMe([checklistPack]).Contains("Send the zips"), "");
    }

    private static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : "";
}
