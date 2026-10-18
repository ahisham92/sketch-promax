using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace SketchProMax.Revit;

/// <summary>
/// Small per-model settings Sketch ProMax remembers between calls (a title block's drawing area, the detail families a
/// project uses), kept as text values in one DataStorage element so they travel with the model and its users.
/// Reads need nothing. Tools remember with <see cref="TryRemember"/> after their own work is committed: in a workshared
/// model the holder can be borrowed by someone else, and a write to it inside the tool's transaction would roll the
/// whole operation back.
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

    /// <summary>
    /// Stores values in a transaction of their own. Call with no transaction open, after the tool's work is committed.
    /// Returns null when stored (or nothing changed), else why not: the model's work stays as it is either way.
    /// </summary>
    public static string? TryRemember(Document doc, IReadOnlyCollection<KeyValuePair<string, string?>> values)
    {
        var changed = values.Where(kv => Get(doc, kv.Key) != kv.Value).ToList();
        if (changed.Count == 0) return null;
        if (doc.IsReadOnly) return "the model is read-only";
        if (doc.IsModifiable) return "a transaction is still open";
        if (Holder(doc) is { } holder)
        {
            if (Blocked(doc, holder.Id) is { } why) return why;
            // Borrowed before the transaction, in one request that respects a pause (CentralAccess): left to the commit,
            // Revit would borrow it on its own and show "Unable to Access the Model" whenever the central model is busy.
            if (CentralAccess.Borrow(doc, [holder.Id]) is { } central) return "Sketch ProMax " + central;
        }
        var errors = new List<string>();
        try
        {
            using var t = new Transaction(doc, "Sketch ProMax: remember settings");
            var options = t.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new RollBackOnError(errors));
            options.SetClearAfterRollback(true);
            options.SetForcedModalHandling(false);
            t.SetFailureHandlingOptions(options);
            FailureGuard.Muted++;
            try
            {
                t.Start();
                foreach (var kv in changed) Set(doc, kv.Key, kv.Value);
                if (t.Commit() == TransactionStatus.Committed) return null;
            }
            finally { FailureGuard.Muted--; }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex) { errors.Add(ex.Message.Trim()); }
        return errors.Count > 0 ? string.Join(" ", errors.Take(2)) : "Revit did not save it";
    }

    /// <summary>Why another user's hold on the element keeps this user from changing it, or null when it can be changed.</summary>
    private static string? Blocked(Document doc, ElementId id)
    {
        if (!doc.IsWorkshared) return null;
        try
        {
            var status = WorksharingUtils.GetCheckoutStatus(doc, id, out var owner);
            if (status == CheckoutStatus.OwnedByOtherUser)
                return $"the model's Sketch ProMax settings are borrowed by {(string.IsNullOrEmpty(owner) ? "another user" : owner)}";
            var updates = WorksharingUtils.GetModelUpdatesStatus(doc, id);
            if (updates is ModelUpdatesStatus.UpdatedInCentral or ModelUpdatesStatus.DeletedInCentral)
                return "the model's Sketch ProMax settings changed in the central model; synchronize first";
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        return null;
    }

    private sealed class RollBackOnError(List<string> errors) : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var error = false;
            foreach (var f in accessor.GetFailureMessages())
            {
                if (f.GetSeverity() == FailureSeverity.Warning) { accessor.DeleteWarning(f); continue; }
                error = true;
                var text = f.GetDescriptionText();
                if (!errors.Contains(text)) errors.Add(text);
            }
            return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }

    /// <summary>Stores a value (null removes it). Needs an open transaction.</summary>
    private static void Set(Document doc, string key, string? value)
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
