using SketchProMax.Tools;

/// <summary>What a failed snippet's result adds so the next try is right: the closest real API members and known fixes.</summary>
internal static class ScriptHintTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var textNote = new[] { "HorizontalAlignment", "VerticalAlignment", "LeaderLeftAttachment", "LeaderRightAttachment", "AddLeader", "GetLeaders", "Text", "Coord", "Width" };
        var close = ScriptSource.Closest("LeftAttachment", textNote);
        check("code hints: a member that doesn't exist names the real ones like it", close.Count > 0 && close[0] == "LeaderLeftAttachment", string.Join(", ", close));
        var revision = new[] { "RevisionNumber", "SequenceNumber", "Description", "RevisionDate", "Issued", "NumberType", "Visibility" };
        check("code hints: a near miss ranks first", ScriptSource.Closest("NumberTyp", revision).FirstOrDefault() == "NumberType", string.Join(", ", ScriptSource.Closest("NumberTyp", revision)));
        check("code hints: nothing alike gives nothing", ScriptSource.Closest("Zzyzx", revision).Count == 0, string.Join(", ", ScriptSource.Closest("Zzyzx", revision)));
        check("code hints: known runtime failures get their fix",
            ScriptSource.RuntimeHint("The input point lies outside of Revit design limits.").Contains("32 km")
            && ScriptSource.RuntimeHint("This operation is not valid for when the revision numbering is per sheet.").Contains("GetRevisionNumberOnSheet")
            && ScriptSource.RuntimeHint("Current Zoom Type is NOT Zoom.").Contains("ZoomType.Zoom")
            && ScriptSource.RuntimeHint("Object reference not set to an instance of an object.").Contains("null")
            && ScriptSource.RuntimeHint("Something new") == "", ScriptSource.RuntimeHint("Current Zoom Type is NOT Zoom."));
    }
}
