using SketchProMax.Chat;

/// <summary>
/// An expired sign-in (bug-log row 71): Claude Code's "Failed to authenticate: OAuth session expired" is seen as a lost
/// sign-in (Autopilot pauses), and the chat treats the saved sign-in as broken until it is renewed or an answer works.
/// </summary>
internal static class SignInTests
{
    public static void Run(Action<string, bool, string> check)
    {
        const string said = "Failed to authenticate: OAuth session expired and could not be refreshed";
        check("signin: the expired-OAuth error is a lost sign-in", SignInHealth.LooksExpired(said) && Autopilot.LostAccess(said), said);
        check("signin: a usage limit is not a lost sign-in", !Autopilot.LostAccess("Claude usage limit reached; resets at 5pm"), "");
        check("signin: a view that could not be refreshed is not a lost sign-in", !Autopilot.LostAccess("The section view could not be refreshed, so I made it again."), "");

        var temp = Path.Combine(Path.GetTempPath(), "spm-signin-" + Guid.NewGuid().ToString("N"));
        var oldFolder = SignInHealth.Folder;
        var oldConfig = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Directory.CreateDirectory(Path.Combine(temp, "config"));
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(temp, "config"));
            SignInHealth.Folder = Path.Combine(temp, "chat");
            SignInHealth.Reset();
            var creds = SignInHealth.CredentialsFile;
            File.WriteAllText(creds, "{\"old\":1}");
            check("signin: nothing known is not broken", !SignInHealth.Broken, "");
            SignInHealth.MarkFailed();
            check("signin: a failed message makes the saved sign-in broken", SignInHealth.Broken, "");
            SignInHealth.Reset(); // as after a Revit restart
            check("signin: still broken after a restart", SignInHealth.Broken, "");
            File.WriteAllText(creds, "{\"new\":12345}");
            File.SetLastWriteTimeUtc(creds, DateTime.UtcNow.AddMinutes(1));
            check("signin: signing in again (a new saved sign-in) clears it", !SignInHealth.Broken, "");
            SignInHealth.MarkFailed();
            SignInHealth.Clear();
            check("signin: an answer that works clears it", !SignInHealth.Broken, "");
        }
        finally
        {
            SignInHealth.Folder = oldFolder;
            SignInHealth.Reset();
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", oldConfig);
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }
}
