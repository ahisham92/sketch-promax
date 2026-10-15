using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// Finds detail-item (and other family) types by the name a user typed, on top of <see cref="TypeMatch"/>:
/// also "Family Type" written with a space instead of a colon, and numbered copies of a family that Revit makes when
/// the same family arrives twice (copy/paste between models): "DET_Rebar_51_Dar" and "DET_Rebar_51_Dar 1".
/// When both exist, the result says so, because existing items may use either.
/// </summary>
internal static class DetailTypes
{
    private static readonly Regex CopySuffix = new(@"\s+\d+$", RegexOptions.Compiled);

    /// <summary>A family name without a trailing copy number: "DET_Rebar_51_Dar 1" -> "DET_Rebar_51_Dar".</summary>
    public static string Base(string? name) => CopySuffix.Replace((name ?? "").Trim(), "");

    private static bool Same(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    public static FamilySymbol Find(Document doc, BuiltInCategory category, string? name, string what, out string? note)
    {
        note = null;
        var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(category).Cast<FamilySymbol>()
            .OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();
        if (symbols.Count == 0) throw new InvalidOperationException($"No {what} family is loaded. Load one with load_family first.");
        if (name is null) return symbols[0];

        if (TypeMatch.TryFind(symbols, name, what, out var match, out var error))
        {
            note = Siblings(symbols, match!);
            return match!;
        }
        var wanted = name.Trim();
        var hits = symbols.Where(s => Same($"{s.FamilyName} {s.Name}", wanted)).ToList();
        if (hits.Count == 0)
        {
            var colon = wanted.IndexOf(':');
            hits = colon > 0
                ? symbols.Where(s => Same(Base(s.FamilyName), Base(wanted.Substring(0, colon))) && Same(s.Name, wanted.Substring(colon + 1))).ToList()
                : symbols.Where(s => Same(Base(s.FamilyName), Base(wanted))).ToList();
        }
        if (hits.Count == 1)
        {
            note = $"'{name}' was matched to '{TypeMatch.Label(hits[0])}'.";
            var sib = Siblings(symbols, hits[0]);
            if (sib != null) note += " " + sib;
            return hits[0];
        }
        if (hits.Count > 1)
            throw new ArgumentException($"'{name}' matches {hits.Count} {what} types: {TypeMatch.Short(hits)}. Give it as 'Family: Type' (numbered family copies such as 'X 1' are separate families).");
        throw new ArgumentException(error);
    }

    /// <summary>A warning when numbered copies of the same family are loaded side by side.</summary>
    private static string? Siblings(IReadOnlyList<FamilySymbol> all, FamilySymbol match)
    {
        var b = Base(match.FamilyName);
        var others = all.Where(s => s.FamilyName != match.FamilyName && Same(Base(s.FamilyName), b)).Select(s => s.FamilyName).Distinct().ToList();
        return others.Count == 0 ? null
            : $"Also loaded as separate numbered copies: '{string.Join("', '", others)}'. Existing items may use those; pass that exact family name to use it.";
    }

    /// <summary>
    /// Every type a name could mean, for filtering existing elements: the exact match plus numbered family copies,
    /// "Family Type" with a space, and (when nothing else fits) a partial match.
    /// </summary>
    public static List<ElementType> Matching(IEnumerable<ElementType> types, string name)
    {
        var all = types.ToList();
        var wanted = name.Trim();
        var colon = wanted.IndexOf(':');
        List<ElementType> hits = colon > 0
            ? all.Where(t => Same(Base(t.FamilyName), Base(wanted.Substring(0, colon))) && Same(t.Name, wanted.Substring(colon + 1))).ToList()
            : all.Where(t => Same(Base(t.FamilyName), Base(wanted)) || Same(t.FamilyName, wanted) || Same(t.Name, wanted)
                             || Same($"{t.FamilyName} {t.Name}", wanted)).ToList();
        if (hits.Count == 0)
            hits = all.Where(t => TypeMatch.Label(t).IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        return hits;
    }
}

/// <summary>
/// Sets parameter values and checks they really took. Revit only settles many values (formulas, constraints, values
/// on a family instance placed in the same transaction) when it regenerates, and an edit can be refused only at
/// commit ("Can't keep elements joined", "Can't make type"), so:
///   1. each element's values are written together in one transaction (fast path);
///   2. after commit (which regenerates), every value is read back; an element whose transaction Revit refused, or a
///      value that came back different, is written again one parameter per transaction (each step regenerates);
///   3. what still differs is reported with the value Revit now holds and why, read from the family when possible
///      (driven by a formula, a reporting parameter, read-only).
/// Uses <see cref="ItemBatch"/>, so locked elements are skipped with their owner and one refusal undoes nothing else.
/// Call with no transaction open.
/// </summary>
internal static class ParamEdits
{
    public sealed record Want(string Name, string Value);

    public sealed class Target
    {
        public required ElementId Id { get; init; }
        public required string Label { get; init; }
        public required List<Want> Wants { get; init; }
    }

    public sealed record Line(long element_id, string parameter, string value, string status, string? now, string? note)
    {
        public bool Ok => status.StartsWith("ok", StringComparison.Ordinal);
    }

    private sealed record Snapshot(StorageType Kind, double D, int I, string? S, long E);

    private sealed class Slot(Target t, Want w)
    {
        public Target T { get; } = t;
        public Want W { get; } = w;
        public string? Status;    // null = written, waiting for read-back
        public string? Note;
        public Snapshot? Snap;
        public bool Stepped;
    }

    public static List<Line> Apply(Document doc, string name, IReadOnlyList<Target> targets, bool separateSteps = false, bool borrow = true)
    {
        var slots = targets.SelectMany(t => t.Wants.Select(w => new Slot(t, w))).ToList();
        if (slots.Count == 0) return [];
        var locked = Batch.Locked(doc, targets.Select(t => t.Id));
        foreach (var s in slots)
        {
            if (doc.GetElement(s.T.Id) is null) s.Status = "element not found";
            else if (locked.TryGetValue(s.T.Id.Value, out var why)) s.Status = "locked: " + why;
        }

        var retry = new List<Slot>();
        if (separateSteps) retry.AddRange(slots.Where(s => s.Status is null));
        else
        {
            var run = targets.Where(t => slots.Any(s => s.T == t && s.Status is null)).ToList();
            var bySlot = slots.ToLookup(s => s.T);
            var a = ItemBatch.Run(doc, name, run, t => t.Label, t => [t.Id], t =>
            {
                var e = doc.GetElement(t.Id);
                foreach (var s in bySlot[t].Where(s => s.Status is null)) Write(e, s);
                return null;
            }, borrow);
            borrow = false;
            for (var i = 0; i < run.Count; i++)
            {
                var outcome = a.Items[i];
                foreach (var s in bySlot[run[i]])
                {
                    if (s.Status is not null) continue;               // read-only, not found, rejected: final
                    if (!outcome.Ok) { s.Note = $"together with the other values Revit refused it: {outcome.Error}"; retry.Add(s); }
                    else if (!Matches(doc, s)) retry.Add(s);
                }
            }
        }

        if (retry.Count > 0)
        {
            foreach (var s in retry) { s.Status = null; s.Stepped = true; }
            var b = ItemBatch.Run(doc, name + " (step by step)", retry, s => $"{s.T.Label} {s.W.Name}", s => [s.T.Id],
                s => { Write(doc.GetElement(s.T.Id), s); return null; }, borrow);
            for (var i = 0; i < retry.Count; i++)
                if (!b.Items[i].Ok && retry[i].Status is null)
                {
                    retry[i].Status = "failed";
                    retry[i].Note = b.Items[i].Error;
                }
        }

        var explain = new FamilyFormulas(doc);
        var lines = new List<Line>();
        foreach (var s in slots)
        {
            var e = doc.GetElement(s.T.Id);
            var p = e is null ? null : Find(e, s.W.Name);
            var now = p is null ? null : RevitHelpers.ParamValue(p);
            string status;
            var note = s.Note;
            if (s.Status is null)
            {
                if (p is not null && Matches(doc, s)) status = s.Stepped ? "ok (applied as a separate step)" : "ok";
                else
                {
                    status = "value did not stick";
                    note = explain.Why(e, s.W.Name) ?? "Revit changed it when regenerating: a constraint, a lock or another parameter drives it.";
                }
            }
            else
            {
                status = s.Status;
                if (s.Status == "read-only") note = explain.Why(e, s.W.Name) ?? note;
            }
            lines.Add(new Line(s.T.Id.Value, s.W.Name, s.W.Value, status, now, note));
        }
        explain.Dispose();
        return lines;
    }

    /// <summary>The element's parameter with this name, preferring a writable one; case-insensitive.</summary>
    public static Parameter? Find(Element e, string name)
    {
        var ps = e.GetParameters(name);
        if (ps.Count == 0)
            ps = e.Parameters.Cast<Parameter>().Where(p => p.Definition != null && string.Equals(p.Definition.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return ps.FirstOrDefault(p => !p.IsReadOnly) ?? ps.FirstOrDefault();
    }

    private static void Write(Element? e, Slot s)
    {
        if (e is null) { s.Status = "element not found"; return; }
        var p = Find(e, s.W.Name);
        if (p is null)
        {
            var type = e.Document.GetElement(e.GetTypeId());
            s.Status = "parameter not found";
            if (type is not null && Find(type, s.W.Name) is not null)
                s.Note = "It is a type parameter: set it on the type (set_parameters with type=true), which changes every instance of that type.";
            return;
        }
        if (p.IsReadOnly) { s.Status = "read-only"; return; }
        bool ok;
        try { ok = SetParametersTool.Write(p, s.W.Value); }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or FormatException)
        {
            s.Status = "rejected"; s.Note = ex.Message.Trim(); return;
        }
        if (!ok) { s.Status = "rejected"; s.Note = $"Revit did not accept '{s.W.Value}' for this {p.StorageType} parameter."; return; }
        s.Snap = new Snapshot(p.StorageType,
            p.StorageType == StorageType.Double ? p.AsDouble() : 0,
            p.StorageType == StorageType.Integer ? p.AsInteger() : 0,
            p.StorageType == StorageType.String ? p.AsString() : null,
            p.StorageType == StorageType.ElementId ? p.AsElementId().Value : 0);
        s.Status = null;
    }

    /// <summary>Whether the committed value is what was written.</summary>
    private static bool Matches(Document doc, Slot s)
    {
        if (s.Snap is null || doc.GetElement(s.T.Id) is not { } e || Find(e, s.W.Name) is not { } p) return false;
        return s.Snap.Kind switch
        {
            StorageType.Double => Math.Abs(p.AsDouble() - s.Snap.D) <= 1e-6 * Math.Max(1, Math.Abs(s.Snap.D)),
            StorageType.Integer => p.AsInteger() == s.Snap.I,
            StorageType.String => string.Equals(p.AsString() ?? "", s.Snap.S ?? "", StringComparison.Ordinal),
            StorageType.ElementId => p.AsElementId().Value == s.Snap.E,
            _ => true,
        };
    }

    /// <summary>Reads formulas and reporting flags from loaded families (opened for reading, never changed), a few per call.</summary>
    private sealed class FamilyFormulas(Document doc) : IDisposable
    {
        private const int MaxFamilies = 5;
        private readonly Dictionary<long, Dictionary<string, string>?> _cache = new();

        public string? Why(Element? e, string name)
        {
            var family = e switch
            {
                FamilyInstance fi => fi.Symbol?.Family,
                FamilySymbol fs => fs.Family,
                _ => null,
            };
            if (family is null) return null;
            if (family.IsInPlace) return "It belongs to an in-place family, whose parameters the API can't inspect.";
            if (!_cache.TryGetValue(family.Id.Value, out var map))
            {
                map = _cache.Count >= MaxFamilies || doc.IsModifiable || !family.IsEditable ? null : Read(family);
                _cache[family.Id.Value] = map;
            }
            if (map is null) return null;
            return map.TryGetValue(name, out var why) ? why : null;
        }

        private Dictionary<string, string>? Read(Family family)
        {
            Document? fdoc = null;
            try
            {
                fdoc = doc.EditFamily(family);
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (FamilyParameter fp in fdoc.FamilyManager.Parameters)
                {
                    var n = fp.Definition?.Name;
                    if (n is null) continue;
                    if (fp.IsReporting) map[n] = $"'{n}' is a reporting parameter in family '{family.Name}' (measured from the geometry), so it can't be set.";
                    else if (fp.IsDeterminedByFormula || !string.IsNullOrEmpty(fp.Formula))
                        map[n] = $"'{n}' is driven by the formula '{fp.Formula}' in family '{family.Name}': change the parameters the formula uses instead.";
                    else if (!fp.IsInstance) map[n] = $"'{n}' is a type parameter in family '{family.Name}': set it on the type (type=true).";
                }
                return map;
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException) { return null; }
            finally { try { fdoc?.Close(false); } catch (Autodesk.Revit.Exceptions.ApplicationException) { } }
        }

        public void Dispose() => _cache.Clear();
    }

    /// <summary>Parameter values from a JSON object (name -> value), as strings.</summary>
    public static List<Want> FromJson(JsonElement obj) =>
        obj.ValueKind != JsonValueKind.Object ? []
            : obj.EnumerateObject().Select(kv => new Want(kv.Name,
                kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() ?? "" : kv.Value.GetRawText())).ToList();

    /// <summary>Lines worth showing: everything that is not plainly ok.</summary>
    public static List<Line> Problems(IEnumerable<Line> lines) => lines.Where(l => l.status != "ok").ToList();
}

/// <summary>
/// Places detail items, either new (NewFamilyInstance) or by copying an existing instance of the type and then
/// moving, rotating and resizing the copy. Copying is the reliable path for families that refuse a fresh placement
/// ("Can't make type", family constraint errors): the copy starts from a state Revit has already solved.
/// </summary>
internal static class DetailPlacement
{
    public sealed class Spec
    {
        public int Index;
        public string? FamilyName;
        public FamilySymbol? Symbol;
        public string? TypeNote;
        public XYZ? At, Start, End;
        public double? RotationDeg;
        public List<ParamEdits.Want> Wants = [];
        public string? Error;
        public ElementId? Created;
        public string? Method;
        public long? Source;
        public string? FallbackReason;
        public List<ParamEdits.Line> Params = [];
    }

    public static Spec Parse(Document doc, View view, JsonElement p, int index, string? defaultName, Dictionary<string, (FamilySymbol, string?)> cache)
    {
        var spec = new Spec { Index = index };
        try
        {
            var name = p.TryGetProperty("family", out var f) ? f.GetString() : defaultName;
            spec.FamilyName = name;
            if (!cache.TryGetValue(name ?? "", out var found))
            {
                var s = DetailTypes.Find(doc, BuiltInCategory.OST_DetailComponents, name, "detail item", out var note);
                cache[name ?? ""] = found = (s, note);
            }
            (spec.Symbol, spec.TypeNote) = found;
            if (p.TryGetProperty("at", out var at)) spec.At = ViewPoints.ToModel(doc, view, at);
            if (p.TryGetProperty("start", out var st)) spec.Start = ViewPoints.ToModel(doc, view, st);
            if (p.TryGetProperty("end", out var en)) spec.End = ViewPoints.ToModel(doc, view, en);
            if (p.TryGetProperty("rotation_degrees", out var r) && r.ValueKind == JsonValueKind.Number) spec.RotationDeg = r.GetDouble();
            if (p.TryGetProperty("parameters", out var ps)) spec.Wants = ParamEdits.FromJson(ps);
            if (spec.At is null && spec.Start is null) throw new ArgumentException("Give 'at' (point-based) or 'start' and 'end' (line-based).");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException or FormatException)
        {
            spec.Error = ex.Message.Trim();
        }
        return spec;
    }

    /// <summary>
    /// Places every spec. method: "new", "copy", or "auto" (new first; on a refusal, copy an existing instance).
    /// Runs in one undo step; call with no transaction open.
    /// </summary>
    public static void Run(Document doc, View view, List<Spec> specs, string method, long? sourceId)
    {
        var todo = specs.Where(s => s.Error is null).ToList();
        if (method != "copy")
        {
            var a = ItemBatch.Run(doc, "Place detail items", todo, s => $"placement {s.Index}", _ => [], s => New(doc, view, s), borrow: false);
            for (var i = 0; i < todo.Count; i++)
            {
                if (a.Items[i].Ok) { todo[i].Created = (ElementId)a.Items[i].Value!; todo[i].Method = "new"; }
                else if (method == "new") todo[i].Error = a.Items[i].Error;
                else todo[i].FallbackReason = "New placement refused: " + a.Items[i].Error;
            }
            Parameters(doc, todo.Where(s => s.Created is not null).ToList());

            if (method == "auto")
            {
                // A fresh instance whose sizes Revit refused gets replaced by a copy of one that already works.
                var refused = todo.Where(s => s.Created is not null && s.Params.Any(l => l.status == "failed")).ToList();
                var replace = refused.Where(s => DetailCopy.Source(doc, s.Symbol!, view, sourceId, out _) is not null).ToList();
                if (replace.Count > 0)
                {
                    var del = ItemBatch.Run(doc, "Replace refused detail items", replace, s => $"placement {s.Index}", s => [s.Created!],
                        s => { doc.Delete(s.Created!); return null; }, borrow: false);
                    for (var i = 0; i < replace.Count; i++)
                        if (del.Items[i].Ok)
                        {
                            replace[i].FallbackReason = "Revit refused the sizes on a new instance: " +
                                string.Join("; ", replace[i].Params.Where(l => l.status == "failed").Select(l => $"{l.parameter}: {l.note}"));
                            replace[i].Created = null;
                            replace[i].Params = [];
                        }
                }
                todo = todo.Where(s => s.Created is null).ToList();
            }
            else todo = [];
        }

        if (todo.Count == 0) return;
        var withSource = new List<(Spec spec, FamilyInstance src, string? how)>();
        foreach (var s in todo)
        {
            var src = DetailCopy.Source(doc, s.Symbol!, view, sourceId, out var how);
            if (src is null)
            {
                s.Error = (s.FallbackReason is null ? "" : s.FallbackReason + " ") +
                          $"No existing '{TypeMatch.Label(s.Symbol!)}' instance (or other type of that family) to copy in this model; place one by hand once, then copies work.";
                continue;
            }
            withSource.Add((s, src, how));
        }
        var b = ItemBatch.Run(doc, "Place detail items by copying", withSource, w => $"placement {w.spec.Index}", _ => [],
            w => DetailCopy.Place(doc, view, w.src, w.spec), borrow: false);
        for (var i = 0; i < withSource.Count; i++)
        {
            var (s, src, how) = withSource[i];
            if (b.Items[i].Ok)
            {
                s.Created = (ElementId)b.Items[i].Value!;
                s.Method = "copy";
                s.Source = src.Id.Value;
                if (how is not null) s.FallbackReason = (s.FallbackReason is null ? "" : s.FallbackReason + " ") + how;
            }
            else s.Error = (s.FallbackReason is null ? "" : s.FallbackReason + " Copying also failed: ") + b.Items[i].Error;
        }
        Parameters(doc, withSource.Select(w => w.spec).Where(s => s.Created is not null).ToList());
    }

    private static void Parameters(Document doc, List<Spec> specs)
    {
        var targets = specs.Where(s => s.Wants.Count > 0).Select(s => new ParamEdits.Target
        {
            Id = s.Created!, Label = $"placement {s.Index}", Wants = s.Wants,
        }).ToList();
        if (targets.Count == 0) return;
        var lines = ParamEdits.Apply(doc, "Detail item parameters", targets, borrow: false);
        foreach (var s in specs) s.Params = lines.Where(l => l.element_id == s.Created!.Value).ToList();
    }

    private static ElementId New(Document doc, View view, Spec s)
    {
        var symbol = s.Symbol!;
        if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
        FamilyInstance inst;
        if (symbol.Family.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail)
        {
            if (s.Start is null || s.End is null) throw new ArgumentException($"'{symbol.FamilyName}' is line-based: give start and end.");
            if (s.Start.DistanceTo(s.End) < doc.Application.ShortCurveTolerance) throw new ArgumentException("start and end are the same point.");
            inst = doc.Create.NewFamilyInstance(Line.CreateBound(s.Start, s.End), symbol, view);
        }
        else
        {
            var point = s.At ?? s.Start!;
            inst = doc.Create.NewFamilyInstance(point, symbol, view);
            if (s.RotationDeg is { } r && Math.Abs(r) > 1e-9)
                ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateUnbound(point, view.ViewDirection), r * Math.PI / 180);
        }
        return inst.Id;
    }

    /// <summary>One result row per placement.</summary>
    public static object Row(Spec s) => new
    {
        index = s.Index,
        id = s.Created?.Value,
        type = s.Symbol is null ? null : TypeMatch.Label(s.Symbol),
        method = s.Method,
        copiedFrom = s.Source,
        why = s.Method == "copy" ? s.FallbackReason : null,
        error = s.Created is null ? s.Error : null,
        typeNote = s.TypeNote,
        parameters = s.Params.Count == 0 ? null : s.Params.Select(l => new { l.parameter, l.value, l.status, l.now, l.note }),
    };
}

internal static class DetailCopy
{
    /// <summary>
    /// An existing instance to copy: the given one, else one of the same type in the view, else in the model, else one of
    /// another type of the same family (the copy is then switched to the wanted type). Grouped instances are avoided.
    /// </summary>
    public static FamilyInstance? Source(Document doc, FamilySymbol symbol, View view, long? sourceId, out string? how)
    {
        how = null;
        if (sourceId is { } sid)
        {
            if (doc.GetElement(new ElementId(sid)) is not FamilyInstance given || given.OwnerViewId == ElementId.InvalidElementId)
                throw new ArgumentException($"{sid} is not a detail item (a view-specific family instance).");
            return given;
        }
        FamilyInstance? Pick(IEnumerable<Element> items) =>
            items.OfType<FamilyInstance>().Where(fi => fi.OwnerViewId != ElementId.InvalidElementId)
                .OrderBy(fi => fi.GroupId != ElementId.InvalidElementId).ThenBy(fi => fi.OwnerViewId != view.Id).FirstOrDefault();

        var ofType = new FamilyInstanceFilter(doc, symbol.Id);
        var hit = Pick(new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_DetailComponents).WherePasses(ofType).ToElements())
                  ?? Pick(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DetailComponents).WherePasses(ofType).ToElements());
        if (hit is not null) return hit;
        var sameFamily = symbol.Family.GetFamilySymbolIds().Where(id => id != symbol.Id).ToList();
        foreach (var other in sameFamily)
        {
            hit = Pick(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DetailComponents).WherePasses(new FamilyInstanceFilter(doc, other)).ToElements());
            if (hit is not null)
            {
                how = $"Copied an instance of type '{hit.Symbol.Name}' of the same family and switched it to '{symbol.Name}'.";
                return hit;
            }
        }
        return null;
    }

    /// <summary>In-view angle (radians) of an instance's x axis, measured from the view's right direction.</summary>
    public static double Angle(View v, FamilyInstance fi)
    {
        var dir = fi.Location is LocationCurve lc && lc.Curve is Line l ? l.Direction : fi.HandOrientation;
        return Math.Atan2(dir.DotProduct(v.UpDirection), dir.DotProduct(v.RightDirection));
    }

    private static XYZ OnPlane(XYZ p, XYZ origin, XYZ normal) => p - normal * (p - origin).DotProduct(normal);

    /// <summary>Copies the source into the view and puts the copy where the spec says. Runs inside the item's transaction.</summary>
    public static ElementId Place(Document doc, View view, FamilyInstance src, DetailPlacement.Spec s)
    {
        var srcView = doc.GetElement(src.OwnerViewId) as View ?? throw new InvalidOperationException("The source item's view no longer exists.");
        var srcAngle = Angle(srcView, src);
        XYZ? srcVector = null; // line-based: the source's line in its own view's axes
        if (src.Location is LocationCurve slc)
        {
            var d = slc.Curve.GetEndPoint(1) - slc.Curve.GetEndPoint(0);
            srcVector = new XYZ(d.DotProduct(srcView.RightDirection), d.DotProduct(srcView.UpDirection), 0);
        }

        ElementId copyId;
        if (srcView.Id == view.Id)
        {
            var from = src.Location switch { LocationPoint lp => lp.Point, LocationCurve lc => lc.Curve.GetEndPoint(0), _ => XYZ.Zero };
            var to = s.At ?? s.Start!;
            copyId = ElementTransformUtils.CopyElement(doc, src.Id, OnPlane(to, from, view.ViewDirection) - from)
                .FirstOrDefault(id => doc.GetElement(id) is FamilyInstance) ?? throw new InvalidOperationException("Revit made no copy.");
        }
        else
        {
            var options = new CopyPasteOptions();
            options.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
            copyId = ElementTransformUtils.CopyElements(srcView, [src.Id], view, null, options)
                .FirstOrDefault(id => doc.GetElement(id) is FamilyInstance) ?? throw new InvalidOperationException("Revit made no copy in this view.");
        }
        var copy = (FamilyInstance)doc.GetElement(copyId);
        if (copy.Symbol.Id != s.Symbol!.Id)
        {
            if (!s.Symbol.IsActive) s.Symbol.Activate();
            copy.ChangeTypeId(s.Symbol.Id);
        }
        doc.Regenerate();

        var normal = view.ViewDirection;
        if (copy.Location is LocationCurve lc2)
        {
            var origin = lc2.Curve.GetEndPoint(0);
            XYZ a, b;
            if (s.Start is not null && s.End is not null) { a = s.Start; b = s.End; }
            else
            {
                a = s.At ?? s.Start!;
                var v = srcVector ?? new XYZ(lc2.Curve.Length, 0, 0);
                if (s.RotationDeg is { } r)
                {
                    var len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
                    var ang = r * Math.PI / 180;
                    v = new XYZ(len * Math.Cos(ang), len * Math.Sin(ang), 0);
                }
                b = a + view.RightDirection * v.X + view.UpDirection * v.Y;
            }
            a = OnPlane(a, origin, normal);
            b = OnPlane(b, origin, normal);
            if (a.DistanceTo(b) < doc.Application.ShortCurveTolerance) throw new ArgumentException("start and end are the same point.");
            lc2.Curve = Line.CreateBound(a, b);
        }
        else if (copy.Location is LocationPoint lp2)
        {
            var target = OnPlane(s.At ?? s.Start!, lp2.Point, normal);
            if (!target.IsAlmostEqualTo(lp2.Point)) ElementTransformUtils.MoveElement(doc, copy.Id, target - lp2.Point);
            doc.Regenerate();
            var want = s.RotationDeg is { } r ? r * Math.PI / 180 : srcAngle;
            var delta = want - Angle(view, copy);
            while (delta > Math.PI) delta -= 2 * Math.PI;
            while (delta < -Math.PI) delta += 2 * Math.PI;
            if (Math.Abs(delta) > 1e-9) ElementTransformUtils.RotateElement(doc, copy.Id, Line.CreateUnbound(target, normal), delta);
        }
        return copy.Id;
    }

    private sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
    }
}

public sealed class PlaceDetailItemCopyTool : IRevitTool
{
    public string Name => "place_detail_item_copy";
    public string Description =>
        "Place detail items (e.g. rebar link / dot families) by copying an existing instance and then moving, rotating and resizing the copy. " +
        "Use it when place_detail_components fails with 'Can't make type' or family constraint errors: a copy starts from a solved instance. " +
        "Give source_id, or the type ('Family: Type') and an instance of it (or of another type of the same family) is found in the view, " +
        "then the model. Each placement: 'at' (point-based) or 'start'/'end' (line-based; 'at' alone keeps the source's length and direction), " +
        "optional rotation_degrees (absolute, in the view; default: as the source) and parameters. Parameter values are set after placing, " +
        "read back after regeneration, re-applied once one by one if needed, and anything that still differs is reported with the reason.";
    public string PropertiesJson => """
    {
      "view_id": {"type": "integer", "description": "Default: active view."},
      "source_id": {"type": "integer", "description": "The detail item instance to copy (any view)."},
      "family": {"type": "string", "description": "Detail item 'Family: Type' (or family name) used when a placement doesn't name one."},
      "placements": {"type": "array", "items": {"type": "object", "properties": {
        "family": {"type": "string"},
        "at": {"type": "array", "items": {"type": "number"}},
        "start": {"type": "array", "items": {"type": "number"}},
        "end": {"type": "array", "items": {"type": "number"}},
        "rotation_degrees": {"type": "number"},
        "parameters": {"type": "object", "description": "Instance parameters to set, name -> value as typed in Revit."}
      }}}
    }
    """;
    public string[] Required => ["placements"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        if (view.IsTemplate || view is View3D || view is ViewSchedule || view.ViewType == ViewType.DrawingSheet)
            return ToolResult.Error($"Detail items go in plan, section, elevation, drafting or detail views, not '{view.Name}' ({view.ViewType}).");
        var sourceId = args.OptLong("source_id");
        var defaultName = args.OptString("family");
        if (sourceId is { } sid && defaultName is null)
        {
            if (doc.GetElement(new ElementId(sid)) is not FamilyInstance src || src.OwnerViewId == ElementId.InvalidElementId)
                return ToolResult.Error($"{sid} is not a detail item instance.");
            defaultName = TypeMatch.Label(src.Symbol);
        }
        var raw = args.Array("placements").ToList();
        if (raw.Count == 0) return ToolResult.Error("Give at least one placement.");
        var cache = new Dictionary<string, (FamilySymbol, string?)>(StringComparer.OrdinalIgnoreCase);
        var specs = raw.Select((p, i) => DetailPlacement.Parse(doc, view, p, i, defaultName, cache)).ToList();

        Batch.InGroup(doc, "Place detail items by copying", () => { DetailPlacement.Run(doc, view, specs, "copy", sourceId); return specs; },
            s => s.Any(x => x.Created is not null));
        var placed = specs.Where(s => s.Created is not null).ToList();
        var payload = new
        {
            view = view.Name,
            placed = placed.Count,
            ids = placed.Select(s => s.Created!.Value),
            placements = specs.Select(DetailPlacement.Row),
            dependentViewWarning = view.GetPrimaryViewId() != ElementId.InvalidElementId
                ? "This is a dependent view: detail items belong to the primary view and show in every dependent view whose crop covers them." : null,
        };
        return placed.Count == 0
            ? new ToolResult("No detail items were placed. " + JsonSerializer.Serialize(payload), IsError: true)
            : ToolResult.Ok(payload);
    }
}
