using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace SketchProMax.Chat;

/// <summary>
/// The classic Outlook on this PC, for the autopilot's questions (AskUser): an email to the user's own address, and the
/// replies to it read back from the Inbox and moved to a "Sketch ProMax" folder. Outlook is reached by COM (late bound,
/// so no Office reference is needed); Outlook starts in the background when it isn't open. The new Outlook has no COM:
/// the classic one only needs a mail profile set up. Every call runs on a thread of its own and gives up after a minute.
/// </summary>
internal static class OutlookMail
{
    /// <summary>One reply found: its subject, its text and its id (to move it).</summary>
    internal sealed record Reply(string Subject, string Body, DateTime Received);

    private const int OlMailItem = 0, OlFolderInbox = 6;

    /// <summary>Sends an email to the user's own address. Null when sent, else why not.</summary>
    public static string? SendToSelf(string subject, string body) => Sta(() =>
    {
        var app = Application();
        var me = OwnAddress(Get(app, "Session"));
        if (me.Length == 0) return "Outlook has no account to send from.";
        var mail = Call(app, "CreateItem", OlMailItem);
        Set(mail, "To", me);
        Set(mail, "Subject", subject);
        Set(mail, "Body", body);
        Call(mail, "Send");
        return (string?)null;
    });

    /// <summary>
    /// The replies (RE: …) in the Inbox whose subject carries one of <paramref name="tokens"/>, each one read. Once the user
    /// replied to an email, that email and the replies move to the Sketch ProMax folder; an email not replied to stays in
    /// the Inbox, where the user reads it. Null when Outlook can't be reached.
    /// </summary>
    public static List<Reply>? TakeReplies(IReadOnlyCollection<string> tokens) => Sta(() =>
    {
        var replies = new List<Reply>();
        if (tokens.Count == 0) return replies;
        var app = Application();
        var session = Get(app, "Session");
        var inbox = Call(session, "GetDefaultFolder", OlFolderInbox);
        var items = Get(inbox, "Items");
        var found = Call(items, "Restrict", "@SQL=\"urn:schemas:httpmail:subject\" LIKE '%" + AskByEmail.SubjectTag.Trim('[', ']') + "%'");
        var count = (int)Get(found, "Count");
        var mails = new List<(object Mail, string Token)>();
        for (var i = 1; i <= count; i++)
        {
            var mail = Call(found, "Item", i);
            var token = AskByEmail.TokenOf(Get(mail, "Subject") as string ?? "");
            if (token is not null && tokens.Contains(token)) mails.Add((mail, token));
        }
        var replied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (mail, token) in mails)
        {
            var subject = Get(mail, "Subject") as string ?? "";
            if (!AskByEmail.IsReply(subject, token)) continue;
            replies.Add(new Reply(subject, Get(mail, "Body") as string ?? "", Get(mail, "ReceivedTime") is DateTime at ? at : DateTime.Now));
            replied.Add(token);
        }
        // Answered: the email and its replies are filed together (the email isn't read again: only replies are).
        object? folder = null;
        foreach (var (mail, token) in mails.Where(m => replied.Contains(m.Token)))
        {
            try
            {
                folder ??= OwnFolder(inbox);
                Call(mail, "Move", folder);
            }
            catch (Exception) { /* left in the Inbox */ }
        }
        return replies;
    });

    private static object Application()
    {
        var type = Type.GetTypeFromProgID("Outlook.Application") ?? throw new InvalidOperationException("The classic Outlook isn't installed.");
        return Activator.CreateInstance(type) ?? throw new InvalidOperationException("Outlook didn't start.");
    }

    /// <summary>The user's own SMTP address (Exchange users' primary address, else the account's).</summary>
    private static string OwnAddress(object session)
    {
        var entry = Get(Get(session, "CurrentUser"), "AddressEntry");
        try
        {
            if (Call(entry, "GetExchangeUser") is { } exchange && Get(exchange, "PrimarySmtpAddress") is string smtp && smtp.Contains('@')) return smtp;
        }
        catch (Exception) { /* not an Exchange account */ }
        return Get(entry, "Address") as string ?? "";
    }

    private static object OwnFolder(object inbox)
    {
        var folders = Get(inbox, "Folders");
        try { return Call(folders, "Item", AskByEmail.Folder); }
        catch (Exception) { return Call(folders, "Add", AskByEmail.Folder); }
    }

    private static object Get(object target, string name) => target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null)!;
    private static void Set(object target, string name, object value) => target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value]);
    private static object Call(object target, string name, params object[] args) => target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args)!;

    /// <summary>Runs <paramref name="work"/> on a thread of its own (COM wants one); default when it fails or takes over a minute.</summary>
    private static T? Sta<T>(Func<T> work)
    {
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true, Name = "Sketch ProMax Outlook" };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(1))) error = new TimeoutException("Outlook didn't answer within a minute.");
        LastError = error is null ? "" : (error is TargetInvocationException { InnerException: { } inner } ? inner.Message : error.Message);
        return error is null ? result : default;
    }

    /// <summary>Why the last call failed ("" when it worked), for the chat's note.</summary>
    public static string LastError { get; private set; } = "";
}
