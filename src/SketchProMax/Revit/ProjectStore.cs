using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace SketchProMax.Revit;

/// <summary>
/// Small per-model settings Sketch ProMax remembers between calls (a title block's drawing area, the detail families a
/// project uses), kept as text values in one DataStorage element so they travel with the model and its users.
/// Reads need nothing; writes need an open transaction.
/// </summary>
internal static class ProjectStore
{
    private static readonly Guid SchemaGuid = new("9d2e4f61-3b7a-4c58-8e19-a6c40b5d7f23");
    private const string Field = "Values";

    private static Schema Schema() => Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid) ?? Build();

    private static Schema Build()
    {
        var b = new SchemaBuilder(SchemaGuid);
        b.SetSchemaName("SketchProMaxProjectSettings");
        b.SetVendorId("SPMX");
        b.SetReadAccessLevel(AccessLevel.Public);
        b.SetWriteAccessLevel(AccessLevel.Public);
        b.AddMapField(Field, typeof(string), typeof(string));
        return b.Finish();
    }

    private static DataStorage? Holder(Document doc)
    {
        var schema = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid);
        if (schema is null) return null;
        return new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
            .FirstOrDefault(d => d.GetEntity(schema).IsValid());
    }

    public static string? Get(Document doc, string key)
    {
        try
        {
            if (Holder(doc) is not { } holder) return null;
            var map = holder.GetEntity(Schema()).Get<IDictionary<string, string>>(Field);
            return map != null && map.TryGetValue(key, out var v) ? v : null;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
    }

    /// <summary>Stores a value (null removes it). Needs an open transaction.</summary>
    public static void Set(Document doc, string key, string? value)
    {
        var schema = Schema();
        var holder = Holder(doc) ?? DataStorage.Create(doc);
        var entity = holder.GetEntity(schema);
        var map = entity.IsValid() ? new Dictionary<string, string>(entity.Get<IDictionary<string, string>>(Field) ?? new Dictionary<string, string>()) : new Dictionary<string, string>();
        if (value is null) map.Remove(key);
        else map[key] = value;
        if (!entity.IsValid()) entity = new Entity(schema);
        entity.Set<IDictionary<string, string>>(Field, map);
        holder.SetEntity(entity);
    }
}
