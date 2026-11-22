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

        // Bug 137: the office web filter's "403 Web Page Blocked" (and a dropped connection) is the network, not the sign-in.
        const string blocked = "Failed to authenticate. API Error: 403 Web Page Blocked";
        check("signin: a web filter's blocked page is not a lost sign-in",
            SignInHealth.LooksBlocked(blocked) && !SignInHealth.LooksExpired(blocked) && !Autopilot.LostAccess(blocked), blocked);
        foreach (var net in new[] { "API Error: Connection error.", "fetch failed: ECONNRESET", "API Error: 403 <html><body>Access denied</body></html>",
                     "unable to get local issuer certificate", "getaddrinfo ENOTFOUND api.anthropic.com" })
            check("signin: a network failure is not a lost sign-in: " + net, SignInHealth.LooksBlocked(net) && !Autopilot.LostAccess(net), net);
        check("signin: a real expired sign-in is not a network block", !SignInHealth.LooksBlocked(said) && !SignInHealth.LooksBlocked("Not logged in · Please run /login"), "");
        check("signin: a 403 from the service itself is still a sign-in problem",
            SignInHealth.LooksExpired("Failed to authenticate. API Error: 403 {\"type\":\"error\",\"error\":{\"type\":\"permission_error\",\"message\":\"OAuth token has been revoked\"}}"), "");
        var summary = SignInHealth.JobNetBlocked(blocked);
        check("signin: a blocked job says the network did it, the sign-in is fine, and it tries again",
            SignInHealth.IsNetBlocked(summary) && summary.Contains("403 Web Page Blocked") && summary.Contains("sign-in is fine") && !summary.Contains("Claude")
            && !SignInHealth.LooksExpired(summary) && !Autopilot.LostAccess(summary), summary);
        var chat = SignInHealth.ChatNetBlocked(blocked);
        check("signin: the chat's network message offers no Sign in again", chat.Contains("sign-in is fine") && !chat.Contains("Sign in again") && !chat.Contains("Claude"), chat);
        check("signin: a blocked job waits 1, 2, then 4 minutes, three times",
            SignInHealth.NetRetries == 3 && SignInHealth.NetRetryWait(1) == TimeSpan.FromMinutes(1) && SignInHealth.NetRetryWait(2) == TimeSpan.FromMinutes(2)
            && SignInHealth.NetRetryWait(3) == TimeSpan.FromMinutes(4), "");
        check("signin: the job's carry-on message is not a sign-in error", !SignInHealth.LooksExpired(SignInHealth.NetCarryOn) && !Autopilot.LostAccess(SignInHealth.NetCarryOn), "");

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
