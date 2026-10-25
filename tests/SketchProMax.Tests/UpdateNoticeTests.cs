using SketchProMax.Loader;
using SketchProMax.UI;

/// <summary>
/// Saying when there is an update: the Update badge's texts, when a check is due, the update notice (once per version,
/// never while something runs, the top lines of the notes) and the Sketch ProMax panel after a live switch.
/// </summary>
internal static class UpdateNoticeTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // ---- The badge says only what is known
        var now = new DateTime(2026, 10, 17, 8, 28, 0, DateTimeKind.Utc);
        var v30 = new Version(0, 30, 0);
        string One((string Text, UpdateBadge.Kind Kind) b) => b.Text.Replace('\n', ' ');
        var checking = UpdateBadge.For("Checking…", null, false, v30, v30, now.AddMinutes(-20), null, now);
        check("badge: checking", One(checking) == "Checking…" && checking.Kind == UpdateBadge.Kind.Working, One(checking));
        var ready = UpdateBadge.For(null, "0.31.0", false, v30, v30, now.AddMinutes(-20), null, now);
        check("badge: update ready with its version", One(ready) == "Update ready 0.31.0" && ready.Kind == UpdateBadge.Kind.Ready, One(ready));
        check("badge: update ready wins over restart, what's new and a failed check",
            UpdateBadge.For(null, "0.31.0", true, v30, new Version(0, 29, 0), now, "timeout", now).Kind == UpdateBadge.Kind.Ready, "");
        var updated = UpdateBadge.For(null, null, false, v30, new Version(0, 29, 0), now.AddMinutes(-20), null, now);
        check("badge: updated, with the version", One(updated) == "Updated to 0.30.0" && updated.Kind == UpdateBadge.Kind.WhatsNew, One(updated));
        var upToDate = UpdateBadge.For(null, null, false, v30, v30, now.AddMinutes(-20), null, now);
        var clock = UpdateBadge.Clock(now.AddMinutes(-20));
        check("badge: up to date with the time of the check", One(upToDate) == $"Up to date (checked {clock})" && upToDate.Kind == UpdateBadge.Kind.UpToDate
            && clock.Length == 5 && clock[2] == ':', One(upToDate));
        var failed = UpdateBadge.For(null, null, false, v30, v30, now.AddMinutes(-20), "GitHub did not answer", now);
        check("badge: couldn't check when the last check failed (never up to date)", One(failed) == "Couldn't check" && failed.Kind == UpdateBadge.Kind.Failed, One(failed));
        var tip = UpdateBadge.Tip(UpdateBadge.Kind.Failed, "0.30.0", "GitHub did not answer.", now.AddMinutes(-20), "", null, null);
        check("badge: couldn't check gives the reason in the tooltip", tip.Contains("GitHub did not answer. Click to try again.") && tip.Contains(clock), tip);
        check("badge: never checked is not up to date", UpdateBadge.For(null, null, false, v30, v30, null, null, now).Kind == UpdateBadge.Kind.Check, "");
        check("badge: a check from last night is not up to date", UpdateBadge.For(null, null, false, v30, v30, now.AddHours(-7.5), null, now).Kind == UpdateBadge.Kind.Check, "");
        check("badge: a check stamped in the future is not up to date", UpdateBadge.For(null, null, false, v30, v30, now.AddHours(2), null, now).Kind == UpdateBadge.Kind.Check, "");
        check("badge: every text fits a large button (two short lines)", new[] { checking, ready, updated, upToDate, failed,
            UpdateBadge.For(null, null, false, v30, v30, null, null, now), UpdateBadge.For(null, null, true, v30, v30, now, null, now) }
            .All(b => b.Text.Split('\n').Length <= 2 && b.Text.Split('\n').All(l => l.Length <= 18)), "");

        // ---- When the next check is due: by the wall clock, so a night asleep doesn't hold it back
        var hour = TimeSpan.FromHours(1);
        check("check due: none yet", UpdateBadge.CheckDue(null, hour, now), "");
        check("check due: not within the hour", !UpdateBadge.CheckDue(now.AddMinutes(-59), hour, now), "");
        check("check due: an hour or more after the last (e.g. after waking from sleep)", UpdateBadge.CheckDue(now.AddHours(-1), hour, now)
            && UpdateBadge.CheckDue(now.AddHours(-9), hour, now), "");
        check("check due: the clock went back", UpdateBadge.CheckDue(now.AddHours(3), hour, now), "");

        // ---- The notice: once per version per session, never while something runs
        check("notice: shows when nothing runs", UpdateNotice.Decide("0.32.0", [], busy: false) == UpdateNotice.Decision.Show, "");
        check("notice: waits while a tool or job runs", UpdateNotice.Decide("0.32.0", [], busy: true) == UpdateNotice.Decision.Wait, "");
        var shown = UpdateNotice.Remember(null, "0.32.0");
        check("notice: once per version", UpdateNotice.Decide("0.32.0", UpdateNotice.Versions(shown), busy: false) == UpdateNotice.Decision.Skip
            && UpdateNotice.Decide("v0.32", UpdateNotice.Versions(shown), busy: false) == UpdateNotice.Decision.Skip, shown);
        check("notice: an already shown version is skipped even while busy", UpdateNotice.Decide("0.32.0", UpdateNotice.Versions(shown), busy: true) == UpdateNotice.Decision.Skip, "");
        var both = UpdateNotice.Remember(UpdateNotice.Remember(shown, "0.32.0"), "0.33.0");
        check("notice: a newer version shows again; the list holds each version once", both == "0.32.0|0.33.0"
            && UpdateNotice.Decide("0.33.1", UpdateNotice.Versions(both), false) == UpdateNotice.Decision.Show, both);

        var available = new UpdateNotice.Notice(UpdateNotice.Kind.Available, "0.32.0", []);
        var applied = new UpdateNotice.Notice(UpdateNotice.Kind.Updated, "0.32.0", [], null);
        var installed = new UpdateNotice.Notice(UpdateNotice.Kind.Installed, "0.32.0", [], "Restart Revit to finish.");
        check("notice: titles", available.Title == "Sketch ProMax 0.32.0 is available" && applied.Title == "Updated to 0.32.0"
            && installed.Title == "Sketch ProMax 0.32.0 is installed" && installed.Next == "Restart Revit to finish.", available.Title);

        const string notes = """
            ## What's new in 0.32.0

            - **Update notice**: says when a new version is out, see [the guide](https://example.com/guide).
            * Ribbon labels stay on narrow screens
            ---
            + Third line
            + Fourth line
            """;
        var top = UpdateNotice.TopLines(notes);
        check("notice: top lines of the notes, as plain text", top.SequenceEqual(["What's new in 0.32.0",
            "Update notice: says when a new version is out, see the guide.", "Ribbon labels stay on narrow screens"]), string.Join(" | ", top));
        const string releaseNotes = """
            **Install:** extract the zip and double-click Install.cmd, or open PowerShell and paste this line (no .cmd needed):

            ```
            irm https://raw.githubusercontent.com/owner/repo/main/get.ps1 | iex
            ```

            If Install.cmd says Windows cannot access the file: Properties > Unblock on the zip, or use the PowerShell line.

            ## What's Changed
            * Berth status by someone in https://example.com/pull/35

            **Full Changelog**: https://example.com/compare/v1...v2
            """;
        var changed = UpdateNotice.TopLines(releaseNotes);
        check("notice: the install help and changelog link are left out", changed.SequenceEqual(["What's Changed",
            "Berth status by someone in https://example.com/pull/35"]), string.Join(" | ", changed));
        check("notice: an update applied in place with nothing to do shows no toast; one needing a restart does",
            !UpdateNotice.WorthShowing(UpdateNotice.Kind.Updated, null) && !UpdateNotice.WorthShowing(UpdateNotice.Kind.Updated, " ")
            && UpdateNotice.WorthShowing(UpdateNotice.Kind.Updated, "Restart Revit for the new ribbon.")
            && UpdateNotice.WorthShowing(UpdateNotice.Kind.Available, null) && UpdateNotice.WorthShowing(UpdateNotice.Kind.Installed, null), "");
        check("notice: no notes, no lines; long lines cut", UpdateNotice.TopLines(null).Count == 0
            && UpdateNotice.TopLines(new string('x', 400))[0].Length == 140, "");

        // ---- Every control labelled, short labels in the stacks; Update and Job live in the first panel (0.35)
        var all = RibbonManifest.Parse(CoreRibbonManifest.Text);
        var group = all.Controls.Where(c => c.Group == "Claude").ToList();
        check("ribbon: Update and Job are labelled live buttons stacked in the first panel", group.Where(c => c.Id is "UpdateClaude" or "JobLive")
            .Count(c => c.Small && c.LiveKey is not null && c.Text.Length > 0) == 2, string.Join(",", group.Select(c => c.Id)));
        check("ribbon: the Update button pumps the update notice", group.Any(c => c.Id == "UpdateClaude" && c.LiveKey == "update-badge"), "");
        var small = all.Controls.Where(c => c.Small).ToList();
        check("ribbon: stacked labels are short, one line", small.All(c => c.Text.Length <= 18 && !c.Text.Contains('\n')),
            string.Join(",", small.Where(c => c.Text.Length > 18 || c.Text.Contains('\n')).Select(c => c.Text)));

        // ---- A live switch: the loader keeps a control in place when its panel, kind and size are the same, else hides
        // it and makes it again (a new command slot); a slot is never reused in the session.
        var now32 = all;
        var was31 = WithLarge(now32, "JobLive", "UpdateClaude");
        var swap = Switch(was31, now32);
        check("live switch from a large Job and Update: they are made again small, the old ones hidden",
            swap.Retired.OrderBy(x => x).SequenceEqual(["JobLive", "UpdateClaude"]), string.Join(",", swap.Retired));
        check("live switch from a large Job and Update: the rest stays where it is", swap.Made.OrderBy(x => x).SequenceEqual(["JobLive", "UpdateClaude"]), string.Join(",", swap.Made));
        // 0.30.0 had no My tools favourites (0.30.1) nor the 0.31 carbon and value-engineering drop-downs.
        var was30 = Without(was31, c => c.Group == "My tools" || c.Id is "UnderDevelopment");
        var from30 = Switch(was30, now32);
        check("live switch from 0.30.0: the Favourites button and its 12 slots are made on My tools",
            from30.Made.Contains("Favourites") && from30.Made.Count(id => id.StartsWith("FavouriteSlot", StringComparison.Ordinal)) == 12, string.Join(",", from30.Made));
        var chain = Switch(was30, was31, now32);
        check("live switches 0.30.0 to 0.31.0 to this version fit the loader's 128 slots", chain.Slots <= CoreRibbonSlots && from30.Slots <= CoreRibbonSlots,
            $"{from30.Slots} / {chain.Slots}");
    }

    private const int CoreRibbonSlots = 128;

    private static RibbonManifest.Parsed WithLarge(RibbonManifest.Parsed m, params string[] ids) => m with
    {
        Controls = m.Controls.Select(c => ids.Contains(c.Id) ? c with { Flags = string.Join(",", c.Flags.Split(',').Where(f => f != "small")) } : c).ToList(),
    };

    private static RibbonManifest.Parsed Without(RibbonManifest.Parsed m, Func<RibbonManifest.Item, bool> drop) => m with
    {
        Controls = m.Controls.Where(c => !drop(c)).ToList(),
        Entries = m.Entries.Where(e => m.Controls.Any(c => c.Id == e.Key && !drop(c))).ToDictionary(e => e.Key, e => e.Value),
    };

    /// <summary>
    /// The loader's CoreRibbon.Sync over a session's manifests, in order: which controls of the last switch were made
    /// again or retired, and how many command slots the session used (active plus spent).
    /// </summary>
    private static (List<string> Made, List<string> Retired, int Slots) Switch(params RibbonManifest.Parsed[] manifests)
    {
        var active = new Dictionary<string, (RibbonManifest.Item Spec, string? Parent)>(StringComparer.Ordinal);
        var spent = 0;
        List<string> made = [], retired = [];
        void Retire(string id)
        {
            if (!active.Remove(id, out var gone)) return;
            if (!gone.Spec.IsDropDown) spent++;
            retired.Add(id);
            foreach (var entry in active.Where(a => a.Value.Parent == id).Select(a => a.Key).ToList()) Retire(entry);
        }
        for (var i = 0; i < manifests.Length; i++)
        {
            made = [];
            retired = [];
            var m = manifests[i];
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in m.Controls)
            {
                wanted.Add(c.Id);
                if (active.TryGetValue(c.Id, out var was))
                {
                    if (was.Spec.Group == c.Group && was.Spec.Kind == c.Kind && was.Spec.Small == c.Small) { active[c.Id] = (c, null); continue; }
                    Retire(c.Id);
                }
                active[c.Id] = (c, null);
                if (i > 0) made.Add(c.Id);
            }
            foreach (var (dropDown, entries) in m.Entries)
                foreach (var e in entries)
                {
                    wanted.Add(e.Id);
                    if (active.TryGetValue(e.Id, out var was) && was.Parent == dropDown) continue;
                    Retire(e.Id);
                    active[e.Id] = (e, dropDown);
                    if (i > 0) made.Add(e.Id);
                }
            foreach (var id in active.Keys.Where(id => !wanted.Contains(id)).ToList()) Retire(id);
        }
        return (made, retired, active.Values.Count(a => !a.Spec.IsDropDown) + spent);
    }
}
