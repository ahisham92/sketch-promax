using SketchProMax.Tools;
using Std = SketchProMax.Tools.DrawingStandard;

/// <summary>
/// The drawing standard learned from issued views, and a new view checked against it (DrawingStandard). No Revit here:
/// the views are made up the way DrawingStandardTool reads them.
/// </summary>
internal static class DrawingStandardTests
{
    private static Std.TagFact Tag(string category, string type, bool leader = false, double u = 0, bool empty = false) =>
        new() { Category = category, Type = type, Leader = leader, Empty = empty, HasBox = true, U0 = u, U1 = u + 1, V0 = 0, V1 = 1 };

    private static Std.ViewFacts Plan(long id, string name, int piles, int tagged, string tag = "Pile Tag: Mark", string dim = "Linear: 2.5mm", int scale = 100, string template = "Piles Plan")
    {
        var v = new Std.ViewFacts { Id = id, Name = name, Kind = "plan", Scale = scale, Template = template };
        for (var i = 0; i < tagged; i++) v.Tags.Add(Tag("Structural Columns", tag, u: i * 3));
        v.Visible["Structural Columns"] = piles;
        v.Tagged["Structural Columns"] = tagged;
        if (dim.Length > 0) v.Dimensions[dim] = 6;
        v.Text["Text: 2.5mm Arial"] = 2;
        return v;
    }

    public static void Run(Action<string, bool, string> check)
    {
        var standard = new Std.Standard { Name = "Quay" };
        Std.Learn(standard, [Plan(1, "BERTH 12 PILES PLAN", 40, 40), Plan(2, "BERTH 13 PILES PLAN", 30, 28), new Std.ViewFacts { Id = 3, Name = "empty", Kind = "plan", Scale = 50 }], "m1");
        var kind = standard.Kinds["plan"];
        check("standard: views with nothing on them teach nothing", kind.Views == 2 && !kind.Scales.ContainsKey("1:50"), kind.Views.ToString());
        check("standard: the tag type, leader and coverage of a category are learned",
            Std.Preferred(standard, "plan", "structural columns") is { Type: "Pile Tag: Mark", Leader: false } && Math.Abs(kind.Tags["Structural Columns"].Coverage() - 68.0 / 70) < 1e-9, "");
        check("standard: nothing known for another kind or category", Std.Preferred(standard, "section", "Structural Columns") is null && Std.Preferred(standard, "plan", "Floors") is null && Std.Preferred(null, "plan", "Floors") is null, "");
        check("standard: the best annotated views are kept as examples", kind.Examples.Select(e => e.Name).SequenceEqual(["BERTH 12 PILES PLAN", "BERTH 13 PILES PLAN"]) && kind.Examples[0].Model == "m1", "");
        Std.Learn(standard, [Plan(1, "BERTH 12 PILES PLAN", 40, 40)], "m1");
        check("standard: learning a view again keeps one example of it", standard.Kinds["plan"].Examples.Count(e => e.Id == 1) == 1, "");

        check("check: a view annotated the same way matches", Std.Check(Plan(9, "BERTH 9 PILES PLAN", 35, 35), standard).Count == 0,
            string.Join(" | ", Std.Check(Plan(9, "BERTH 9 PILES PLAN", 35, 35), standard).Select(i => i.What)));

        var poor = Plan(10, "BERTH 10 PILES PLAN", 35, 5, tag: "Structural Column Tag: Standard", dim: "Linear: 3.5mm Bold", scale: 75, template: "");
        poor.Tags[0].Leader = true;
        poor.Tags[1].Empty = true;
        poor.Tags[2].U0 = poor.Tags[3].U0; poor.Tags[2].U1 = poor.Tags[3].U1;
        var issues = Std.Check(poor, standard);
        bool Says(string what, string fix) => issues.Any(i => i.What.Contains(what) && i.Fix.Contains(fix));
        check("check: another scale and a missing view template are named", Says("Scale is 1:75", "scale") && Says("No view template", "apply_view_template 'Piles Plan'"), string.Join(" | ", issues.Select(i => i.What)));
        check("check: tags of another type are named with the standard's type", Says("5 Structural Columns tag(s) are 'Structural Column Tag: Standard'", "'Pile Tag: Mark'"), "");
        check("check: untagged elements of a category the standard tags", Says("30 of 35 Structural Columns have no tag", "tag_elements_in_view"), "");
        check("check: leaders the standard doesn't use, empty tags and tags on top of each other",
            Says("have leaders", "leaders_off") && Says("1 tag(s) show no text", "parameter") && Says("sit on top of another tag", "fix_overlaps"), "");
        check("check: dimensions of another type", Says("6 dimensions use 'Linear: 3.5mm Bold'", "'Linear: 2.5mm'"), "");
        var bare = Plan(11, "BERTH 11 PILES PLAN", 35, 35, dim: "");
        check("check: no dimensions where issued views have them", Std.Check(bare, standard).Any(i => i.What.StartsWith("No dimensions")), "");
        var section = new Std.ViewFacts { Id = 12, Name = "SECTION A", Kind = "section", Scale = 50 };
        check("check: a kind of view the standard never saw says so, once", Std.Check(section, standard) is [{ } only] && only.What.Contains("no section views") && only.Fix.Contains("add=true"), "");

        // The file: by name, read back, and the standard a model uses.
        standard.Models.Add("m1");
        var other = new Std.Standard { Name = "Self-test standard" };
        var all = Std.Parse(Std.Write(new Dictionary<string, Std.Standard> { [standard.Name] = standard, [other.Name] = other }));
        check("file: written and read back, names and categories case-blind",
            all.Count == 2 && all["quay"].Kinds["PLAN"].Tags["structural columns"].Types["pile tag: mark"] > 0 && all["Quay"].Kinds["plan"].Examples.Count == 2, Std.Write(all).Length.ToString());
        check("file: unreadable text is no standards", Std.Parse("{not json").Count == 0 && Std.Parse(null).Count == 0 && Std.Parse("[null, {\"Name\": \"\"}]").Count == 0, "");
        check("file: a model's standard is the one named, else its own, else the only one in use; never one no model uses",
            Std.For(all, "m1")?.Name == "Quay" && Std.For(all, "m2")?.Name == "Quay" && Std.For(all, "m1", "Self-test standard")?.Name == "Self-test standard"
            && Std.For(all, "m1", "none") is null && Std.For(new Dictionary<string, Std.Standard> { ["x"] = other }, "m1") is null, "");
        var summary = System.Text.Json.JsonSerializer.Serialize(Std.Summary(all["Quay"]));
        check("summary: per kind, the tag per category with its share tagged, and the examples",
            summary.Contains("\"tag\":\"Pile Tag: Mark\"") && summary.Contains("\"tagged_percent\":98") && summary.Contains("BERTH 12 PILES PLAN") && summary.Contains("1:100"), summary);
    }

    /// <summary>Checks on the sources: the tool reads only, tagging takes the standard's type, and the chat is told to use it.</summary>
    public static void RunSources(string root, Action<string, bool, string> check)
    {
        string Read(string path) => File.ReadAllText(Path.Combine(root, path));
        var tool = Read("src/SketchProMax/Tools/DrawingStandardTool.cs");
        check("drawing_standard: reads only, a few views a call, and can be stopped", tool.Contains("public bool ModifiesModel => false;") && tool.Contains("Take(MaxViews)") && tool.Contains("CancelPoint.ThrowIfRequested()"), "");
        var tagging = Read("src/SketchProMax/Tools/DocumentationTools.cs");
        check("tag_elements_in_view: the standard's tag type and leader, unless a tag_type is given",
            tagging.Contains("DrawingStandardTool.Preferred(doc, view") && tagging.Contains("\"tag_type\"") && tagging.Contains("tag.ChangeTypeId(tagType.Id)"), "");
        var prompt = Read("src/SketchProMax/Agent/SystemPrompt.cs");
        check("prompt: annotation follows the drawing standard and every view is checked against it",
            prompt.Contains("drawing_standard action=show") && prompt.Contains("drawing_standard action=check") && prompt.Contains("compare_with"), "");
    }
}
