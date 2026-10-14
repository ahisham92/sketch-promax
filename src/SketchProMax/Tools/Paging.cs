namespace SketchProMax.Tools;

/// <summary>
/// Keeps list results small enough for a chat: an optional name filter, then offset/limit paging, with the total and
/// the next offset reported so Claude can ask for more.
/// </summary>
internal static class Paging
{
    public const string Properties = """
      "name_contains": {"type": "string", "description": "Only items whose name contains this text (case-insensitive)."},
      "offset": {"type": "integer", "description": "Skip this many items (for the next page)."},
      "limit": {"type": "integer", "description": "Max items to return (default 100)."}
    """;

    public static IEnumerable<T> Named<T>(IEnumerable<T> items, ToolArgs args, Func<T, string?> name)
    {
        var text = args.OptString("name_contains");
        return string.IsNullOrWhiteSpace(text)
            ? items
            : items.Where(i => (name(i) ?? "").IndexOf(text!, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public static (List<T> Items, object Info) Page<T>(IEnumerable<T> items, ToolArgs args, int defaultLimit = 100)
    {
        var all = items.ToList();
        var offset = (int)Math.Max(0, args.OptLong("offset") ?? 0);
        var limit = (int)Math.Min(1000, Math.Max(1, args.OptLong("limit") ?? defaultLimit));
        var page = all.Skip(offset).Take(limit).ToList();
        var next = offset + page.Count < all.Count ? offset + page.Count : (int?)null;
        return (page, new
        {
            total = all.Count,
            returned = page.Count,
            next_offset = next,
            note = next is null ? null : $"{all.Count - next} more; call again with offset {next}, or narrow with name_contains.",
        });
    }
}
