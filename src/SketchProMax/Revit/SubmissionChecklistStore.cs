using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using SketchProMax.Tools;

namespace SketchProMax.Revit;

/// <summary>
/// The technical submission checklist's records in the model (SubmissionChecklistMath): one DataStorage element per
/// person, holding their answers, approvals, setup and added questions as JSON. Each person writes only their own
/// element, so an engineer and a group leader never wait for each other's element in a workshared model: Synchronize
/// with Central sends a record, Reload Latest brings the others'. Reads need nothing; writes run in a transaction of
/// their own with no other transaction open (as ProjectStore).
/// </summary>
internal static class SubmissionChecklistStore
{
    private static readonly Guid SchemaGuid = new("4c7b2e90-6d15-4f3a-9b8e-2a61d0f5c3e7");
    private const string UserField = "User", JsonField = "Json";

    private static Schema Schema() => Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid) ?? Build();

    private static Schema Build()
    {
        var b = new SchemaBuilder(SchemaGuid);
        b.SetSchemaName("SketchProMaxSubmissionChecklist");
        b.SetVendorId("SPMX");
        b.SetReadAccessLevel(AccessLevel.Public);
        b.SetWriteAccessLevel(AccessLevel.Public);
        b.AddSimpleField(UserField, typeof(string));
        b.AddSimpleField(JsonField, typeof(string));
        return b.Finish();
    }

    /// <summary>The user name the records go by: Revit's (Options > General), else Windows'.</summary>
    public static string UserOf(Document doc)
    {
        try { if (doc.Application.Username is { Length: > 0 } name) return name.Trim(); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        return Environment.UserName;
    }

    private static IEnumerable<(DataStorage Holder, Entity Entity)> Holders(Document doc)
    {
        var schema = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid);
        if (schema is null) yield break;
        foreach (var d in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>())
        {
            Entity e;
            try { e = d.GetEntity(schema); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; }
            if (e.IsValid()) yield return (d, e);
        }
    }

    /// <summary>Everyone's records in the model (unreadable ones left out).</summary>
    public static List<SubmissionChecklistMath.UserRecord> ReadAll(Document doc)
    {
        var list = new List<SubmissionChecklistMath.UserRecord>();
        foreach (var (_, e) in Holders(doc))
        {
            var user = e.Get<string>(UserField) ?? "";
            if (SubmissionChecklistMath.ReadRecord(e.Get<string>(JsonField) ?? "") is { } r)
            {
                if (r.User.Length == 0) r.User = user;
                list.Add(r);
            }
        }
        return list;
    }

    /// <summary>
    /// Writes the user's own record into the model. Call with no transaction open. Returns null when stored (or
    /// unchanged), else why not; the record is kept on the PC by the caller either way.
    /// </summary>
    public static string? Write(Document doc, SubmissionChecklistMath.UserRecord record)
    {
        if (doc.IsReadOnly) return "the model is read-only";
        if (doc.IsModifiable) return "Revit is in the middle of an edit";
        var json = SubmissionChecklistMath.ToJson(record);
        var mine = Holders(doc).FirstOrDefault(h => SubmissionChecklistMath.Same(h.Entity.Get<string>(UserField) ?? "", record.User));
        if (mine.Holder is not null)
        {
            if ((mine.Entity.Get<string>(JsonField) ?? "") == json) return null;
            if (ProjectStore.Blocked(doc, mine.Holder.Id) is { } why) return why.Replace("Sketch ProMax settings", "checklist answers");
            if (CentralAccess.Borrow(doc, [mine.Holder.Id]) is { } central) return "Sketch ProMax " + central;
        }
        var errors = new List<string>();
        try
        {
            using var t = new Transaction(doc, "Sketch ProMax: submission checklist");
            var options = t.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new ProjectStore.RollBackOnError(errors));
            options.SetClearAfterRollback(true);
            options.SetForcedModalHandling(false);
            t.SetFailureHandlingOptions(options);
            FailureGuard.Muted++;
            try
            {
                t.Start();
                var holder = mine.Holder ?? DataStorage.Create(doc);
                var entity = new Entity(Schema());
                entity.Set(UserField, record.User);
                entity.Set(JsonField, json);
                holder.SetEntity(entity);
                if (t.Commit() == TransactionStatus.Committed) return null;
            }
            finally { FailureGuard.Muted--; }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex) { errors.Add(ex.Message.Trim()); }
        return errors.Count > 0 ? string.Join(" ", errors.Take(2)) : "Revit did not save it";
    }
}
