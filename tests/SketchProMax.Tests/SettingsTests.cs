using System.Text.RegularExpressions;
using SketchProMax.UI;

/// <summary>The Settings window's tabs: every stored setting on exactly one tab, and a size that fits a 1080p screen at 100 % and 150 %.</summary>
internal static class SettingsTests
{
    public static void Run(string repoRoot, Action<string, bool, string> check)
    {
        var keys = SettingsLayout.Tabs.Select(t => t.Key).ToList();
        check("settings: eight tabs, each once", keys.Count == 8 && keys.Distinct().Count() == 8
            && SettingsLayout.Tabs.Select(t => t.Name).SequenceEqual(["Approvals", "Chat", "Firm standards", "Time tracking", "Issue & drawings", "Sign-off", "Lifeline", "Updates"]),
            string.Join(", ", SettingsLayout.Tabs.Select(t => t.Name)));
        check("settings: every setting is on a tab that exists", SettingsLayout.Settings.Values.All(keys.Contains), "");
        var empty = keys.Where(k => !SettingsLayout.Settings.Values.Contains(k)).ToList();
        check("settings: no tab is empty", empty.Count == 0, string.Join(", ", empty));

        // The stored settings (AgentSettings' auto-properties, the legacy switch aside) are exactly the ones laid out.
        var settingsSource = File.ReadAllText(Path.Combine(repoRoot, "src/SketchProMax/Settings/AgentSettings.cs"));
        var stored = Regex.Matches(settingsSource, @"public\s+[\w<>?.]+\s+(\w+)\s*\{\s*get;\s*set;\s*\}").Select(m => m.Groups[1].Value)
            .Where(n => n != "RequireApprovalForChanges").ToHashSet();
        check("settings: every stored setting has a tab", stored.SetEquals(SettingsLayout.Settings.Keys),
            "stored only: " + string.Join(", ", stored.Except(SettingsLayout.Settings.Keys)) + "; laid out only: " + string.Join(", ", SettingsLayout.Settings.Keys.Except(stored)));

        // The window places a control for each, and saves only settings it places (Approvals writes ApprovalLevel).
        var windowSource = File.ReadAllText(Path.Combine(repoRoot, "src/SketchProMax/UI/SettingsWindow.cs"));
        var placed = Regex.Matches(windowSource, @"Place\(""(\w+)""").Select(m => m.Groups[1].Value).ToHashSet();
        check("settings: the window has a control for every setting", placed.SetEquals(SettingsLayout.Settings.Keys),
            "missing: " + string.Join(", ", SettingsLayout.Settings.Keys.Except(placed)) + "; unknown: " + string.Join(", ", placed.Except(SettingsLayout.Settings.Keys)));
        var saved = Regex.Matches(windowSource, @"_settings\.(\w+)\s*=[^=]").Select(m => m.Groups[1].Value == "Approvals" ? "ApprovalLevel" : m.Groups[1].Value).ToHashSet();
        check("settings: everything saved is on a tab", saved.Count >= 10 && saved.IsSubsetOf(placed), string.Join(", ", saved.Except(placed)));

        // Size: at most 85 % of the work area, Save always on screen, the page keeping room at the smallest size.
        foreach (var (name, w, h) in new[] { ("1080p at 100 %", 1920.0, 1040.0), ("1080p at 125 %", 1536.0, 826.0), ("1080p at 150 %", 1280.0, 688.0), ("tiny", 800.0, 450.0) })
        {
            var fit = SettingsLayout.Fit(w, h);
            check($"settings: the window fits {name}", fit.Width <= w * 0.85 && fit.Height <= h * 0.85 && fit.MinWidth <= fit.Width && fit.MinHeight <= fit.Height,
                $"{fit.Width}x{fit.Height} min {fit.MinWidth}x{fit.MinHeight}");
        }
        var full = SettingsLayout.Fit(1920, 1040);
        check("settings: full size on a 1080p screen at 100 %", full is { Width: 780, Height: 640 }, full.ToString());
        var scaled = SettingsLayout.Fit(1280, 688);
        check("settings: at 150 % the page keeps room under the header and above Save", scaled.Height - SettingsLayout.Chrome >= SettingsLayout.MinPage * 2
            && SettingsLayout.MinHeight - SettingsLayout.Chrome >= SettingsLayout.MinPage, scaled.ToString());
        check("settings: device pixels to DIPs", SettingsLayout.ToDips(1920, 1032, 1.5) == (1280, 688) && SettingsLayout.ToDips(1920, 1040, 0) == (1920, 1040), "");
        check("settings: unreadable work area gives a small screen's size", SettingsLayout.Fit(0, 0) == SettingsLayout.Fit(1280, 680), "");

        // The tab opened last, and Ctrl+Tab wrapping round.
        check("settings: opens on the tab left last", SettingsLayout.StartIndex("time\r\n") == 3 && SettingsLayout.StartIndex("gone") == 0 && SettingsLayout.StartIndex(null) == 0, "");
        check("settings: Ctrl+Tab wraps both ways", SettingsLayout.Next(7, back: false) == 0 && SettingsLayout.Next(0, back: true) == 7 && SettingsLayout.Next(2, back: false) == 3, "");
    }
}
