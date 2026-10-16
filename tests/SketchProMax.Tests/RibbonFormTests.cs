using SketchProMax.Loader;
using SketchProMax.Tools;
using SketchProMax.UI;

/// <summary>The ribbon's tool options window (schema to fields, typed values to arguments, remembered values), the Update badge and the workflow manifest.</summary>
internal static class RibbonFormTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // ---- Schema to fields
        const string schema = """
        {
          "level": {"type": "string", "description": "Level name."},
          "count": {"type": "integer"},
          "spacing": {"type": ["number", "null"], "default": 1.5},
          "dry_run": {"type": "boolean"},
          "mode": {"type": "string", "enum": ["building", "interior"]},
          "element_ids": {"type": "array", "items": {"type": "integer"}},
          "names": {"type": "array", "items": {"type": "string", "enum": ["A", "B"]}},
          "points": {"type": "array", "items": {"type": "object"}},
          "options": {"type": "object"},
          "job_id": {"type": "integer"}
        }
        """;
        var fields = ToolForm.Fields(schema, ["level", "mode"]);
        ToolForm.Field F(string key) => fields.Single(f => f.Key == key);
        check("form: one field per property, in order", fields.Select(f => f.Key).SequenceEqual(
            ["level", "count", "spacing", "dry_run", "mode", "element_ids", "names", "points", "options", "job_id"]), string.Join(",", fields.Select(f => f.Key)));
        check("form: required marked, label from key", F("level").Required && F("mode").Required && !F("count").Required
            && F("element_ids").Label == "Element ids" && F("dry_run").Label == "Dry run", "");
        check("form: types, union types and defaults", F("count").Type == "integer" && F("spacing").Type == "number"
            && F("spacing").Default == "1.5" && F("dry_run").Type == "boolean" && F("points").ItemType == "object", "");
        check("form: enum becomes a list to pick from", F("mode").Choices!.SequenceEqual(["building", "interior"]) && F("names").Choices is null
            && F("names").Help!.Contains("Options: A, B"), F("names").Help ?? "");
        check("form: element ids offer the selection, job numbers don't", F("element_ids").ElementIds && !F("job_id").ElementIds
            && !F("level").ElementIds && ToolForm.IsElementIds("wall_id", "integer", null) && !ToolForm.IsElementIds("width", "number", null), "");
        check("form: arrays say commas, objects say JSON", F("element_ids").Help!.Contains("commas") && F("options").Help!.Contains("JSON")
            && F("points").Help!.Contains("JSON list"), "");
        check("form: a broken schema gives no fields", ToolForm.Fields("{not json", []).Count == 0 && ToolForm.Fields("", []).Count == 0, "");

        // ---- Starting values
        var remembered = new Dictionary<string, string> { ["level"] = "L2", ["element_ids"] = "1, 2", ["mode"] = "gone" };
        check("form: last values come back, element ids don't", ToolForm.Initial(F("level"), remembered) == "L2"
            && ToolForm.Initial(F("element_ids"), remembered) is null, "");
        check("form: a remembered choice no longer offered falls back", ToolForm.Initial(F("mode"), remembered) == "building", ToolForm.Initial(F("mode"), remembered) ?? "null");
        check("form: schema default when nothing remembered", ToolForm.Initial(F("spacing"), null) == "1.5" && ToolForm.Initial(F("count"), null) is null, "");
        check("form: unknown yes/no is a list with the tool's default", ToolForm.AsYesNoList(F("dry_run"), null) && !ToolForm.AsYesNoList(F("dry_run"), "true")
            && !ToolForm.AsYesNoList(F("count"), null), "");

        // ---- Typed values to arguments
        var problems = new List<string>();
        var json = ToolForm.Arguments(fields, new Dictionary<string, string>
        {
            ["level"] = " L1 ", ["count"] = "3", ["spacing"] = "2.5", ["dry_run"] = "yes", ["mode"] = "interior",
            ["element_ids"] = "101, 102;103", ["names"] = "A, B", ["points"] = "[{\"x\": 0}]", ["options"] = "{\"a\": 1}", ["job_id"] = "",
        }, problems);
        var args = json is null ? null : ToolForm.Parse(json);
        check("args: every type converted", args is not null && args["level"].GetString() == "L1" && args["count"].GetInt64() == 3
            && args["spacing"].GetDouble() == 2.5 && args["dry_run"].GetBoolean() && args["mode"].GetString() == "interior"
            && args["element_ids"].EnumerateArray().Select(e => e.GetInt64()).SequenceEqual([101L, 102L, 103L])
            && args["names"].GetArrayLength() == 2 && args["points"][0].GetProperty("x").GetInt32() == 0 && args["options"].GetProperty("a").GetInt32() == 1,
            json ?? string.Join(" | ", problems));
        check("args: empty values are left out (tool defaults apply)", args is not null && !args.ContainsKey("job_id"), json ?? "");
        problems.Clear();
        var bad = ToolForm.Arguments(fields, new Dictionary<string, string>
        {
            ["count"] = "three", ["spacing"] = "x", ["mode"] = "other", ["element_ids"] = "1, a", ["options"] = "[1]", ["points"] = "1, 2", ["dry_run"] = "maybe",
        }, problems);
        check("args: each unreadable field named with what to type", bad is null && problems.Count == 8
            && problems.Any(p => p.StartsWith("Level: fill this in", StringComparison.Ordinal))
            && problems.Any(p => p.StartsWith("Count:", StringComparison.Ordinal) && p.Contains("whole number"))
            && problems.Any(p => p.StartsWith("Mode:", StringComparison.Ordinal) && p.Contains("building, interior")),
            string.Join(" | ", problems));
        problems.Clear();
        check("args: exponents and JSON arrays accepted", ToolForm.Arguments([F("spacing"), F("element_ids")],
            new Dictionary<string, string> { ["spacing"] = "1e3", ["element_ids"] = "[5, 6]" }, problems) == "{\"spacing\":1000,\"element_ids\":[5, 6]}",
            string.Join(" | ", problems));

        // ---- Remembered values on disk
        var folder = Path.Combine(Path.GetTempPath(), "spm-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(folder, "tool-defaults.json");
            var empty = ToolDefaults.Load(path);
            check("defaults: no file is an empty memory", empty.For("place_columns") is null, "");
            empty.Remember("place_columns", fields, new Dictionary<string, string> { ["level"] = "L3", ["element_ids"] = "9", ["count"] = "4" });
            var saved = empty.Save();
            var again = ToolDefaults.Load(path).For("place_columns");
            check("defaults: saved and read back, element ids left out", saved && again is not null && again["level"] == "L3" && again["count"] == "4"
                && !again.ContainsKey("element_ids"), again is null ? "null" : string.Join(",", again.Select(kv => kv.Key + "=" + kv.Value)));
            var replaced = ToolDefaults.Load(path);
            replaced.Remember("place_columns", fields, new Dictionary<string, string> { ["level"] = "L4" });
            replaced.Save();
            var latest = ToolDefaults.Load(path).For("place_columns");
            check("defaults: a new run replaces the last values", latest is not null && latest.Count == 1 && latest["level"] == "L4", "");
            File.WriteAllText(path, "{ broken");
            check("defaults: a damaged file is an empty memory", ToolDefaults.Load(path).For("place_columns") is null, "");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }

        // ---- Update badge
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var v30 = new Version(0, 30, 0);
        check("badge: working wins", UpdateBadge.For("Downloading\n45%", true, v30, v30, now, false, now) == ("Downloading 45%", UpdateBadge.Kind.Working), "");
        check("badge: restart before what's new", UpdateBadge.For(null, true, v30, new Version(0, 29, 0), now, false, now).Kind == UpdateBadge.Kind.Restart, "");
        check("badge: what's new after an update", UpdateBadge.For(null, false, v30, new Version(0, 29, 0), now, false, now) == ("What's new", UpdateBadge.Kind.WhatsNew), "");
        check("badge: check when never checked, failed or stale", UpdateBadge.For(null, false, v30, v30, null, false, now).Kind == UpdateBadge.Kind.Check
            && UpdateBadge.For(null, false, v30, v30, now, true, now).Kind == UpdateBadge.Kind.Check
            && UpdateBadge.For(null, false, v30, v30, now.AddDays(-3), false, now).Kind == UpdateBadge.Kind.Check, "");
        check("badge: up to date after a recent check", UpdateBadge.For(null, false, v30, v30, now.AddHours(-1), false, now) == ("Up to date", UpdateBadge.Kind.UpToDate), "");

        // ---- The workflow manifest
        var all = RibbonManifest.Parse(CoreRibbonManifest.Text);
        var panels = all.Controls.Select(c => c.Group).Distinct().ToList();
        check("ribbon: panels in workflow order", panels.SequenceEqual(["Claude", "Model", "Documents", "Check", "Issue", "Sketch ProMax"]), string.Join(",", panels));
        check("ribbon: every tool entry runs its tool and needs a model", all.Entries.Values.SelectMany(e => e).Concat(all.Controls)
            .Where(c => c.Command.StartsWith("tool:", StringComparison.Ordinal)).All(c => c.NeedsModel && c.Id == "Tool." + c.Command.Substring(5)), "");
        check("ribbon: every control has a tooltip", all.Controls.Concat(all.Entries.Values.SelectMany(e => e)).All(c => c.Tip.Length > 20),
            string.Join(",", all.Controls.Concat(all.Entries.Values.SelectMany(e => e)).Where(c => c.Tip.Length <= 20).Select(c => c.Id)));
        var units = RibbonManifest.Units(all.Controls.Where(c => c.Group == "Sketch ProMax")).Select(u => u.Count).ToList();
        check("ribbon: Sketch ProMax panel stacks 2, 3, 2", units.SequenceEqual([2, 3, 2]), string.Join(",", units));
        var claudeUnits = RibbonManifest.Units(all.Controls.Where(c => c.Group == "Claude")).Select(u => u.Count).ToList();
        check("ribbon: Claude panel is Chat, then two stacks of two", claudeUnits.SequenceEqual([1, 2, 2]), string.Join(",", claudeUnits));
        var steelGone = RibbonManifest.Parse(CoreRibbonManifest.Build(t => t.Contains("steel") || t == "place_bracing" ? null : "Summary of " + t));
        check("ribbon: missing tools are left out, an emptied drop-down goes too", !steelGone.Controls.Any(c => c.Id == "Steel")
            && !steelGone.Entries.ContainsKey("Steel") && steelGone.Unknown.Count == 0
            && steelGone.Entries["Structure"].First().More == "Summary of place_columns", string.Join("|", steelGone.Unknown));
        var slots = all.Controls.Count(c => !c.IsDropDown) + all.Entries.Values.Sum(e => e.Count);
        check("ribbon: fits the loader's 128 command slots (CoreRibbon.Slots)", slots <= 128, slots.ToString());
    }
}
