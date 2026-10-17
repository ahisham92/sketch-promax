using SketchProMax.Tools;

/// <summary>Type catalogs are checked before Revit reads one: a bad header makes Revit stop on a modal dialog.</summary>
internal static class TypeCatalogTests
{
    public static void Run(Action<string, bool, string> check)
    {
        check("catalog: a header with no parameter is refused", TypeCatalog.HeaderProblem(",") is { } none && none.Contains("names no parameter"), TypeCatalog.HeaderProblem(",") ?? "");
        check("catalog: an empty first line is refused", TypeCatalog.HeaderProblem("") is not null && TypeCatalog.Problem([]) is not null, "");
        check("catalog: a column without name##spec##units is refused", TypeCatalog.HeaderProblem(",b,h") is { } bad && bad.Contains("'b'"), TypeCatalog.HeaderProblem(",b,h") ?? "");
        check("catalog: a valid header passes", TypeCatalog.HeaderProblem(",b##length##millimeters,Code##other##") is null
            && TypeCatalog.HeaderProblem("\"\",\"d##LENGTH##MILLIMETERS\"") is null, TypeCatalog.HeaderProblem(",b##length##millimeters,Code##other##") ?? "");
        string[] good = [",Depth##length##millimeters", "FT 200x100,200", "FT 300x150,300", ""];
        check("catalog: a valid catalog passes and lists its types", TypeCatalog.Problem(good) is null && TypeCatalog.Types(good).SequenceEqual(["FT 200x100", "FT 300x150"]),
            string.Join("|", TypeCatalog.Types(good)));
        check("catalog: the old field-test catalog is refused", TypeCatalog.Problem([",", "FT 200x100,", "FT 300x150,"]) is not null, "");
        check("catalog: a row longer than the header is refused", TypeCatalog.Problem([",d##length##millimeters", "A,1,2"]) is { } longRow && longRow.Contains("line 2"), "");
        check("catalog: quoted cells keep their commas", TypeCatalog.Split("\"W 10x49, heavy\",254") is ["W 10x49, heavy", "254"], string.Join("|", TypeCatalog.Split("\"W 10x49, heavy\",254")));
    }
}
