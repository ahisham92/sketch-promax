using SketchProMax.Tools;
using SketchProMax.UI;

/// <summary>The result window's page (UI/ResultView): figures, the chart, the lists, Select in Revit, and the options window's level fields.</summary>
internal static class ResultViewTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // A model check's result, as check_model returns it.
        var model = ResultView.From("""
            {"overallScore":82,"scores":[{"check":"warnings","findings":143,"checkedItems":900,"score":84},{"check":"duplicates","findings":6,"checkedItems":900,"score":99}],
             "findings":149,"file":"C:\\Users\\me\\Documents\\SketchProMax\\Model check\\Tower - Model check.csv","exceptions":null,
             "warningTypes":[{"warning":"Highlighted walls overlap","count":61},{"warning":"Elements have duplicate Mark","count":38},{"warning":"Beam is slightly off axis","count":27}],
             "sample":[{"Check":"duplicates","Id":412233,"What":"Wall: Basic 200","Issue":"same place as 412241"}]}
            """);
        check("result: a JSON result becomes a page", model is not null, "");
        if (model is null) return;
        check("result: top-level numbers are the figures, labelled in words", model.Figures.Select(f => f.Label + "=" + f.Value).SequenceEqual(["Overall score=82", "Findings=149"]),
            string.Join(",", model.Figures.Select(f => f.Label + "=" + f.Value)));
        check("result: a list of label and count is the bar chart, largest first", model.Bars is { Title: "Warning types" } c && c.Bars.Count == 3 && c.Bars[0] == ("Highlighted walls overlap", 61),
            model.Bars?.Title ?? "no chart");
        var scores = model.Sections.FirstOrDefault(s => s.Title == "Scores");
        check("result: a list of objects is a section of rows named by their label, with their main figure", scores is { Total: 2 } && scores.Rows[0].Text == "warnings" && scores.Rows[0].Figure == "143",
            scores is null ? "none" : string.Join("|", scores.Rows.Select(r => r.Text + ":" + r.Figure)));
        check("result: a row's other values are its details", scores!.Rows[0].Details.Any(d => d.Label == "Checked items" && d.Value == "900") && !scores.Rows[0].Details.Any(d => d.Label == "Check"), "");
        var sample = model.Sections.FirstOrDefault(s => s.Title == "Sample");
        check("result: a row with an element id can select it in Revit", sample is not null && sample.Rows[0].ElementIds.SequenceEqual([412233L]) && sample.Rows[0].Text == "duplicates", "");
        check("result: file paths are offered, not listed as figures", model.Files.Count == 1 && model.Files[0].EndsWith("Model check.csv", StringComparison.Ordinal), string.Join(",", model.Files));
        check("result: nulls are left out", !model.Sections.Any(s => s.Title == "Exceptions"), "");

        var counts = ResultView.From("""{"summary":"Tagged 40 elements.","byCategory":{"Walls":30,"Doors":10},"elementIds":[1,2,3]}""");
        check("result: the summary line shows first", counts?.Summary == "Tagged 40 elements.", counts?.Summary ?? "");
        check("result: an object of numbers is the chart", counts?.Bars is { Title: "By category" } b && b.Bars.Select(x => x.Label).SequenceEqual(["Walls", "Doors"]), "");
        check("result: plain text stays plain text (no page)", ResultView.From("Placed 12 columns.") is null && ResultView.From("{ broken") is null, "");
        var array = ResultView.From("""[{"name":"A-101","revision":"P02"},{"name":"A-102","revision":"P01"}]""");
        check("result: a list on its own is one section", array?.Sections.Single().Rows.Select(r => r.Text).SequenceEqual(["A-101", "A-102"]) == true, "");
        var many = ResultView.From("{\"items\":[" + string.Join(",", Enumerable.Range(1, 500).Select(i => $"{{\"name\":\"n{i}\",\"size\":{i},\"x\":1}}")) + "]}");
        check("result: long lists show the first rows and say how many there are", many?.Sections[0] is { Total: 500 } s5 && s5.Rows.Count == ResultView.MaxRows, "");

        check("result: words from keys", ResultView.Human("overallScore") == "Overall score" && ResultView.Human("checked_items") == "Checked items" && ResultView.Human("BOQ") == "BOQ", ResultView.Human("overallScore"));

        // ---- Options window: level fields get the model's levels
        check("options: base_level, top_level and level are level fields", ToolForm.IsLevel("base_level") && ToolForm.IsLevel("top_level") && ToolForm.IsLevel("level"), "");
        check("options: an offset, count or elevation is not", !ToolForm.IsLevel("level_offset") && !ToolForm.IsLevel("level_count") && !ToolForm.IsLevel("level_elevation"), "");
        check("options: every ribbon tool has a short tip for the window's first line", CoreRibbonManifest.Tools().All(t => t.Tip.Length is > 20 and <= 200),
            string.Join(",", CoreRibbonManifest.Tools().Where(t => t.Tip.Length > 200).Select(t => t.Tool)));
    }
}
