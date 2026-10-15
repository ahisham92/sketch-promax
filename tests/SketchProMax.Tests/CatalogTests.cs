using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Checks every tool the add-in registers without Revit running: names, schemas, descriptions, self-test coverage,
/// documentation counts, and the plain helpers (paging, argument parsing, learned-tool files).
/// Loads a built SketchProMax.dll by reflection; Revit's API assemblies come from the NuGet cache.
/// </summary>
internal static class CatalogTests
{
    private static readonly string[] SchemaTypes = ["string", "number", "integer", "boolean", "array", "object"];

    // Tools the self-test deliberately leaves to a person, with the reason SelfTest.Finish reports.
    private static readonly string[] NotSelfTested = ["batch_run_recipe", "load_family", "copy_from_file", "title_block_from_cad", "load_steel_sections", "run_learned_tool", "run_self_test", "self_test_status"];

    public static void Run(string addinDir, string repoRoot, Action<string, bool, string> check)
    {
        var nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var revitYear = Regex.Match(addinDir, @"20\d\d").Value;
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var name = new AssemblyName(e.Name).Name + ".dll";
            var local = Path.Combine(addinDir, name);
            if (File.Exists(local)) return Assembly.LoadFrom(local);
            if (!Directory.Exists(nuget)) return null;
            var match = Directory.GetDirectories(nuget, "nice3point.revit.api.*")
                .SelectMany(d => Directory.GetDirectories(d).Where(v => Path.GetFileName(v).StartsWith(revitYear)))
                .SelectMany(v => Directory.GetFiles(v, name, SearchOption.AllDirectories))
                .FirstOrDefault();
            return match is null ? null : Assembly.LoadFrom(match);
        };

        var asm = Assembly.LoadFrom(Path.Combine(addinDir, "SketchProMax.dll"));
        Type T(string name) => asm.GetType(name) ?? throw new InvalidOperationException($"{name} not found");
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        var registry = T("SketchProMax.Tools.ToolRegistry").GetMethod("CreateDefault", Any)!.Invoke(null, null)!;
        var tools = ((IEnumerable)registry.GetType().GetProperty("All")!.GetValue(registry)!).Cast<object>().ToList();
        string Prop(object o, string p) => o.GetType().GetProperty(p)!.GetValue(o)?.ToString() ?? "";
        check($"registry builds ({tools.Count} tools)", tools.Count > 50, "");

        // Names
        var names = tools.Select(t => Prop(t, "Name")).ToList();
        var dupes = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        check("tool names are unique", dupes.Count == 0, string.Join(", ", dupes));
        var badNames = names.Where(n => !Regex.IsMatch(n, "^[a-z][a-z0-9_]{2,63}$")).ToList();
        check("tool names are snake_case", badNames.Count == 0, string.Join(", ", badNames));

        // Descriptions and schemas
        var schemaProblems = new List<string>();
        var schemaType = T("SketchProMax.Tools.ToolSchema");
        foreach (var tool in tools)
        {
            var name = Prop(tool, "Name");
            var description = Prop(tool, "Description");
            if (description.Length < 30) schemaProblems.Add($"{name}: description too short");
            if (Regex.IsMatch(description, @"\bTODO\b|\bFIXME\b")) schemaProblems.Add($"{name}: description has TODO");
            JsonElement props;
            try { props = JsonDocument.Parse("{" + "\"p\":" + Prop(tool, "PropertiesJson") + "}").RootElement.GetProperty("p"); }
            catch (JsonException ex) { schemaProblems.Add($"{name}: PropertiesJson is not JSON ({ex.Message})"); continue; }
            if (props.ValueKind != JsonValueKind.Object) { schemaProblems.Add($"{name}: PropertiesJson is not an object"); continue; }
            foreach (var p in props.EnumerateObject()) CheckSchema($"{name}.{p.Name}", p.Value, schemaProblems);
            var required = (string[])tool.GetType().GetProperty("Required")!.GetValue(tool)!;
            foreach (var r in required)
                if (!props.TryGetProperty(r, out _)) schemaProblems.Add($"{name}: required '{r}' is not a property");
            if (required.Distinct().Count() != required.Length) schemaProblems.Add($"{name}: duplicate required entries");

            // What Claude actually receives must serialize.
            try
            {
                var input = schemaType.GetMethod("InputSchema", Any)!.Invoke(null, [tool]);
                var desc = schemaType.GetMethod("Description", Any)!.Invoke(null, [tool]);
                var entry = JsonSerializer.Serialize(new { name, description = desc, inputSchema = input });
                using var parsed = JsonDocument.Parse(entry);
                if (parsed.RootElement.GetProperty("inputSchema").GetProperty("type").GetString() != "object")
                    schemaProblems.Add($"{name}: input schema type is not object");
            }
            catch (Exception ex) { schemaProblems.Add($"{name}: catalogue entry fails ({(ex.InnerException ?? ex).Message})"); }
        }
        check("every tool has a valid schema and description", schemaProblems.Count == 0, "\n  " + string.Join("\n  ", schemaProblems));

        // Self-test coverage. The cases' lambdas touch Revit types, which can't run here, so read them from the source.
        var selfTestSource = File.ReadAllText(Path.Combine(repoRoot, "src", "SketchProMax", "Tools", "SelfTest.cs"));
        var casesStart = selfTestSource.IndexOf("IEnumerable<Case> ReadOnlyCases()", StringComparison.Ordinal);
        var caseSource = casesStart < 0 ? "" : selfTestSource.Substring(casesStart);
        var caseTools = Regex.Matches(caseSource, @"^\s*new\(""([a-z0-9_]+)""", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        check($"self-test cases found ({caseTools.Count})", caseTools.Count > 50, "");
        var uncovered = names.Where(n => !caseTools.Contains(n) && !NotSelfTested.Contains(n)).ToList();
        check("every tool has a self-test case", uncovered.Count == 0, string.Join(", ", uncovered));
        var stale = caseTools.Where(c => !names.Contains(c) && !c.StartsWith("self_test_")).Distinct().ToList();
        check("self-test cases name real tools", stale.Count == 0, string.Join(", ", stale));
        var setups = Regex.Matches(caseSource, @"Setup: \[([^\]]*)\]").SelectMany(m => Regex.Matches(m.Groups[1].Value, @"""([a-z0-9_]+)""").Select(x => x.Groups[1].Value))
            .Distinct().Where(s => !names.Contains(s)).ToList();
        check("self-test setup steps name real tools", setups.Count == 0, string.Join(", ", setups));
        // Regression cases may repeat a tool on purpose; the main lists must not.
        var regressionStart = caseSource.IndexOf("IEnumerable<Case> RegressionCases()", StringComparison.Ordinal);
        var mainTools = Regex.Matches(regressionStart < 0 ? caseSource : caseSource.Substring(0, regressionStart), @"^\s*new\(""([a-z0-9_]+)""", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        var caseDupes = mainTools.GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        check("one self-test case per tool", caseDupes.Count == 0, string.Join(", ", caseDupes));

        // Documentation counts (two connection tools live in the MCP connector)
        var total = tools.Count + 2;
        var readme = File.ReadAllText(Path.Combine(repoRoot, "README.md"));
        check($"README says {total} tools", readme.Contains($"through {total} tools"), Regex.Match(readme, @"through \d+ tools").Value);
        check($"README says {tools.Count} IRevitTool", readme.Contains($"The {tools.Count} IRevitTool"), Regex.Match(readme, @"The \d+ IRevitTool").Value);

        // ToolArgs
        var argsType = T("SketchProMax.Tools.ToolArgs");
        object Args(string json) => Activator.CreateInstance(argsType,
            JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) as IReadOnlyDictionary<string, JsonElement>)!;
        object? Call(object target, string method, params object?[] a) =>
            target.GetType().GetMethods(Any).First(m => m.Name == method && m.GetParameters().Length == a.Length).Invoke(target, a);
        var sample = Args("""{"n": 5, "s": "x", "obj": {"a": 1}, "list": ["a", "b"], "ids": [1, 2], "none": null, "flag": true}""");
        check("ToolArgs reads values",
            Equals(Call(sample, "OptLong", "n"), 5L) && (string?)Call(sample, "String", "s") == "x"
            && (string?)Call(sample, "String", "obj") == "{\"a\": 1}" && Equals(Call(sample, "OptBool", "flag", false), true)
            && ((IList)Call(sample, "StringList", "list")!).Count == 2 && ((IList)Call(sample, "LongList", "ids")!).Count == 2, "");
        check("ToolArgs treats null as missing", Equals(Call(sample, "Has", "none"), false) && Call(sample, "OptString", "none") is null, "");
        var missing = Throws(() => Call(sample, "String", "nope"));
        check("ToolArgs names a missing argument", missing?.Contains("nope") == true, missing ?? "no error");

        // Paging
        var paging = T("SketchProMax.Tools.Paging");
        var page = paging.GetMethod("Page", Any)!.MakeGenericMethod(typeof(int));
        var items = Enumerable.Range(0, 250).ToList();
        var r1 = page.Invoke(null, [items, Args("""{"offset": 100, "limit": 100}"""), 100])!;
        var list1 = (List<int>)r1.GetType().GetField("Item1")!.GetValue(r1)!;
        var info1 = JsonSerializer.Serialize(r1.GetType().GetField("Item2")!.GetValue(r1));
        check("paging returns the requested page", list1.Count == 100 && list1[0] == 100 && info1.Contains("\"next_offset\":200") && info1.Contains("\"total\":250"), info1);
        var r2 = page.Invoke(null, [items, Args("""{"offset": 200}"""), 100])!;
        var info2 = JsonSerializer.Serialize(r2.GetType().GetField("Item2")!.GetValue(r2));
        check("paging ends cleanly", ((List<int>)r2.GetType().GetField("Item1")!.GetValue(r2)!).Count == 50 && info2.Contains("\"next_offset\":null"), info2);
        var r3 = page.Invoke(null, [items, Args("""{"offset": -5, "limit": 0}"""), 100])!;
        check("paging clamps bad offset/limit", ((List<int>)r3.GetType().GetField("Item1")!.GetValue(r3)!).SequenceEqual([0]), "");
        var named = paging.GetMethod("Named", Any)!.MakeGenericMethod(typeof(string));
        var filtered = ((IEnumerable<string>)named.Invoke(null, [new[] { "Level 1", "level 2", "Roof" }, Args("""{"name_contains": "LEVEL"}"""), (Func<string, string?>)(s => s)])!).ToList();
        check("name filter is case-insensitive", filtered.Count == 2, string.Join(",", filtered));

        // Learned tools
        var learned = T("SketchProMax.Tools.LearnedTools");
        var valid = (Regex)learned.GetField("ValidName", Any)!.GetValue(null)!;
        check("learned-tool names: valid ones accepted", new[] { "count_walls", "abc", "room_area_by_level2" }.All(valid.IsMatch), "");
        check("learned-tool names: unsafe ones rejected",
            new[] { "..", "ab", "Count", "1abc", "a/b", "..\\x", "has space", "a-b", new string('a', 49) }.All(n => !valid.IsMatch(n)), "");
        var plain = learned.GetMethod("Plain", Any)!;
        var converted = plain.Invoke(null, [JsonDocument.Parse("""{"a": 1, "b": 2.5, "c": [true, "x"], "d": null}""").RootElement])!;
        var dict = (IDictionary)converted;
        check("learned-tool args become plain values",
            Equals(dict["a"], 1L) && Equals(dict["b"], 2.5) && ((IList)dict["c"]!)[0] is true && dict["d"] is null, JsonSerializer.Serialize(converted));
        var toolType = T("SketchProMax.Tools.LearnedTool");
        var made = toolType.GetMethod("Create", Any)!.Invoke(null, ["count_walls", "Counts walls.", "return 1;",
            """{"level": {"type": "string"}}""", new[] { "level" }, false, 3])!;
        var json = (string)toolType.GetMethod("ToJson", Any)!.Invoke(made, null)!;
        var back = toolType.GetMethod("FromJson", Any)!.Invoke(null, [json])!;
        check("learned tool survives save and load",
            Prop(back, "Name") == "count_walls" && Prop(back, "Code") == "return 1;" && Prop(back, "Uses") == "3"
            && ((string[])toolType.GetProperty("Required")!.GetValue(back)!).SequenceEqual(["level"])
            && Prop(back, "PropertiesJson").Contains("level"), json);
        var forged = json.Replace("\"name\": \"count_walls\"", "\"name\": \"..\\\\..\\\\evil\"");
        var forgedLoad = Throws(() => toolType.GetMethod("FromJson", Any)!.Invoke(null, [forged]));
        check("learned tool with an unsafe name is refused", forgedLoad is not null, "loaded a tool named ..\\..\\evil");
    }

    private static void CheckSchema(string path, JsonElement s, List<string> problems)
    {
        if (s.ValueKind != JsonValueKind.Object) { problems.Add($"{path}: not an object"); return; }
        if (!s.TryGetProperty("type", out var type))
        {
            if (!s.TryGetProperty("anyOf", out _) && !s.TryGetProperty("oneOf", out _)) problems.Add($"{path}: no type");
            return;
        }
        var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()).ToList() : [type.GetString()];
        foreach (var t in types)
            if (!SchemaTypes.Contains(t)) problems.Add($"{path}: unknown type '{t}'");
        if (s.TryGetProperty("description", out var d) && d.ValueKind != JsonValueKind.String) problems.Add($"{path}: description is not text");
        if (s.TryGetProperty("enum", out var en))
        {
            if (en.ValueKind != JsonValueKind.Array || en.GetArrayLength() == 0) problems.Add($"{path}: empty enum");
            else if (types.Contains("string") && en.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String)) problems.Add($"{path}: enum values are not strings");
        }
        if (types.Contains("array"))
        {
            if (!s.TryGetProperty("items", out var items)) problems.Add($"{path}: array without items");
            else CheckSchema(path + "[]", items, problems);
        }
        if (types.Contains("object") && s.TryGetProperty("properties", out var props))
        {
            if (props.ValueKind != JsonValueKind.Object) problems.Add($"{path}: properties is not an object");
            else foreach (var p in props.EnumerateObject()) CheckSchema($"{path}.{p.Name}", p.Value, problems);
            if (s.TryGetProperty("required", out var req))
                foreach (var r in req.EnumerateArray())
                    if (!props.TryGetProperty(r.GetString()!, out _)) problems.Add($"{path}: required '{r}' missing");
        }
    }

    private static string? Throws(Action a)
    {
        try { a(); return null; }
        catch (Exception ex) { return (ex is TargetInvocationException { InnerException: { } inner } ? inner : ex).Message; }
    }
}
