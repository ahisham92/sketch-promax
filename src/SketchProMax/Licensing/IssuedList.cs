using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SketchProMax.Licensing;

/// <summary>
/// The License manager's list of keys given out (owner's PC only, %APPDATA%\SketchProMax\license\issued.json). Making a key,
/// changing its end date and cancelling it happen here; the window (UI/LicenseManagerWindow) only shows and asks.
/// </summary>
internal sealed class IssuedList
{
    internal sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string PcCode { get; set; } = "";
        public DateTime Issued { get; set; }
        public DateTime? Ends { get; set; }
        public string Key { get; set; } = "";
        public string Note { get; set; } = "";
        /// <summary>Set when a newer key replaced this one (Change end date), or "cancelled".</summary>
        public string ReplacedBy { get; set; } = "";
        /// <summary>The change line to publish for this key ("" = none needed), until the owner marks it published.</summary>
        public string PendingChange { get; set; } = "";

        public bool Active => ReplacedBy.Length == 0;

        public string Status(DateTime today) =>
            ReplacedBy == "cancelled" ? "Cancelled" :
            ReplacedBy.Length > 0 ? "Replaced by " + ReplacedBy :
            Ends is { } e && today.Date > e.Date ? "Ended" : "Active";
    }

    public List<Entry> Entries { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static string File => Path.Combine(LicenseState.Folder, "issued.json");

    public static IssuedList Load(string? file = null)
    {
        try
        {
            var path = file ?? File;
            return System.IO.File.Exists(path) ? JsonSerializer.Deserialize<IssuedList>(System.IO.File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(string? file = null)
    {
        var path = file ?? File;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SafeFile.Write(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Makes and lists a key for <paramref name="pcCode"/>. Throws ArgumentException with the user's words when an input is wrong.</summary>
    public Entry Issue(string signingSecret, string pcCode, string name, DateTime? ends, string note, DateTime today)
    {
        var code = LicenseKey.NormalizeCode(pcCode) ?? throw new ArgumentException("The PC code should look like SPM-7K2Q-9XW4-M3TD (12 letters and numbers).");
        var who = LicenseKey.Clean(name);
        if (who.Length == 0) throw new ArgumentException("Type who the key is for.");
        if (ends is { } e && e.Date < today.Date) throw new ArgumentException("The end date is in the past.");
        using var signer = LicenseKey.Signer(signingSecret) ?? throw new ArgumentException("The signing secret on this PC can't be read.");
        var license = new LicenseKey.License(LicenseKey.NewId(), code, who, today.Date, ends?.Date);
        var entry = new Entry
        {
            Id = license.Id, Name = who, PcCode = code, Issued = license.Issued, Ends = license.Ends,
            Key = LicenseKey.Sign(license, signer), Note = (note ?? "").Trim(),
        };
        Entries.Add(entry);
        return entry;
    }

    /// <summary>
    /// A new key with a new end date for the same person and PC. The old key stops being listed as active; when the new date
    /// is earlier than the old key's, the old key needs a published change (it would otherwise keep working until its own date).
    /// </summary>
    public Entry ChangeEnd(string signingSecret, Entry old, DateTime? ends, DateTime today)
    {
        var next = Issue(signingSecret, old.PcCode, old.Name, ends, old.Note, today);
        old.ReplacedBy = next.Id;
        if (Shorter(ends, old.Ends)) old.PendingChange = LicenseChanges.Line(old.Id, ends ?? today.Date.AddDays(-1));
        return next;
    }

    /// <summary>Cancels a key: it needs a published change to stop working on that PC.</summary>
    public void Cancel(Entry entry)
    {
        entry.ReplacedBy = "cancelled";
        entry.PendingChange = LicenseChanges.Line(entry.Id, null);
    }

    /// <summary>True when <paramref name="next"/> ends before <paramref name="old"/> (no date = never ends).</summary>
    internal static bool Shorter(DateTime? next, DateTime? old) => next is { } n && (old is null || n.Date < old.Value.Date);

    /// <summary>The list as CSV (opens in Excel).</summary>
    public string Csv(DateTime today)
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("Name,PC code,Issued,Ends,Status,Key id,Note,Key\r\n");
        foreach (var e in Entries)
            sb.Append(string.Join(",", Q(e.Name), Q(e.PcCode), e.Issued.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                e.Ends?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "No end", Q(e.Status(today)), e.Id, Q(e.Note), Q(e.Key))).Append("\r\n");
        return sb.ToString();
    }
}
