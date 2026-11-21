using System.IO;
using SketchProMax.Phone;

/// <summary>The phone link (Iris): messages sealed the way the website's phone app and other clients read them, and the address the user types.</summary>
internal static class IrisTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var key = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        // Sealed by the Python client with the same key: the C# side must read what the other sides write.
        const string fromPython = "v1.W4V3AsqBpAv3FQDs8OniYE40LMzfcFSyyXtvapOwgOoJ25kfv1qk-kHCGqD-eKNhFtGyFmsNBeZRz3T7Z2utsu0pRQjn4TNNWk_WbtLnJnw";
        check("iris: reads a message sealed by another client", IrisClient.Open(key, fromPython) == "{\"text\": \"3 lengths\"}", IrisClient.Open(key, fromPython));
        var sealedText = IrisClient.Seal(key, "{\"text\":\"PVC pipes: 4 lengths or 3?\"}");
        check("iris: a sealed message carries nothing readable", sealedText.StartsWith("v1.") && !sealedText.Contains("PVC"), sealedText);
        check("iris: seal and open round trip", IrisClient.Open(key, sealedText) == "{\"text\":\"PVC pipes: 4 lengths or 3?\"}", "");
        var tampered = sealedText.Substring(0, sealedText.Length - 3) + (sealedText[sealedText.Length - 3] == 'A' ? "B" : "A") + sealedText.Substring(sealedText.Length - 2);
        var refused = false;
        try { IrisClient.Open(key, tampered); } catch (System.Security.Cryptography.CryptographicException) { refused = true; }
        check("iris: a changed message is refused", refused, tampered);

        check("iris: a bare site gets https and /iris", PhoneLink.Address("name.pythonanywhere.com") == "https://name.pythonanywhere.com/iris", PhoneLink.Address("name.pythonanywhere.com"));
        check("iris: a full address is kept", PhoneLink.Address("https://name.pythonanywhere.com/iris/") == "https://name.pythonanywhere.com/iris", "");
        check("iris: nothing typed is nothing", PhoneLink.Address("  ") == "", "");

        // One phone chat per project (2 Nov 2026): the central model, a local copy and a detached copy are the same project.
        void Project(string source, string want, string user = "")
        {
            var got = PhoneProjects.Guess(source, user);
            check($"phone project: {source} is {want}", got == want, got);
        }
        Project(@"C:\Models\harbour.rvt", "Harbour");
        Project(@"\\server\Projects\HARBOUR.rvt", "Harbour");
        Project("harbour_sam.taylor", "Harbour");
        Project(@"C:\Users\sam\Documents\harbour_sam.rvt", "Harbour", "sam");
        Project("harbour_sam.taylor_detached", "Harbour");
        Project(@"D:\Work\Harbour_detached.rvt", "Harbour");
        Project("Harbour - Copy", "Harbour");
        Project("Tower.0003", "Tower");
        Project("PRJ1-0100D-RVT-HBR-SB", "HBR");
        Project("harbour_documentation_sam.taylor", "Harbour");
        Project("", "");
        check("phone project: a rename gives the chat its name", PhoneProjects.Of("PRJ1-0100D-RVT-HBR-SB", "", "", "TWR = Tower, HBR = Harbour") == "Harbour",
            PhoneProjects.Of("PRJ1-0100D-RVT-HBR-SB", "", "", "TWR = Tower, HBR = Harbour"));
        check("phone project: two names join into one chat", PhoneProjects.Renamed("HAR", "HAR = Harbour\nHarbour2 = Harbour") == "Harbour" && PhoneProjects.Renamed("Harbour2", "HAR = Harbour\nHarbour2 = Harbour") == "Harbour", "");
        check("phone project: no rename keeps the guess", PhoneProjects.Renamed("Northpier", "HAR = Harbour") == "Northpier", "");
        check("phone project: an unsaved model goes by its title", PhoneProjects.Of("unsaved:42", "Northpier_detached") == "Northpier", PhoneProjects.Of("unsaved:42", "Northpier_detached"));
        check("phone project: case doesn't split a chat", PhoneProjects.Same("harbour", "Harbour "), "");

        // Which Revit takes a message from a project's phone chat.
        var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        var claims = new Dictionary<int, PhoneLink.ProjectClaim>
        {
            [100] = new() { Projects = ["Harbour"], Beat = now },
            [200] = new() { Projects = ["Northpier", "Harbour"], Beat = now },
            [300] = new() { Projects = ["Eastbay"], Beat = now.AddHours(-3) },
        };
        bool Alive(int pid) => pid != 400;
        check("phone routing: the main chat goes to the reader", PhoneLink.TopicTaker("", claims, 100, null, Alive, now) == 100, "");
        check("phone routing: a project open in one Revit goes there", PhoneLink.TopicTaker("northpier", claims, 100, null, Alive, now) == 200, "");
        check("phone routing: open in two, the autopilot's Revit takes it", PhoneLink.TopicTaker("Harbour", claims, 100, 200, Alive, now) == 200, "");
        check("phone routing: open in two, no autopilot, the reader takes it", PhoneLink.TopicTaker("Harbour", claims, 200, null, Alive, now) == 200, "");
        check("phone routing: a stale claim doesn't count", PhoneLink.TopicTaker("Eastbay", claims, 100, null, Alive, now) == 100, "");
        claims[400] = new() { Projects = ["Lekki"], Beat = now };
        check("phone routing: a closed Revit doesn't count", PhoneLink.TopicTaker("Lekki", claims, 100, null, Alive, now) == 100, "");

        Resend(check, key);
        Pictures(check);
    }

    /// <summary>The user, 21 Nov 2026: "I don't get pictures or send pictures" on the phone chat.</summary>
    private static void Pictures(Action<string, bool, string> check)
    {
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 1, 2, 3 };
        var url = PhonePictures.DataUrl(bytes, "image/jpeg");
        var got = PhonePictures.Decode(url);
        check("phone pictures: a phone picture is read back", got is { } g && g.Bytes.SequenceEqual(bytes) && g.Extension == ".jpg", url);
        check("phone pictures: a png keeps its kind", PhonePictures.Decode(PhonePictures.DataUrl(bytes, "image/png"))?.Extension == ".png", "");
        check("phone pictures: not a picture is refused", PhonePictures.Decode("data:text/html;base64,PGI+") is null && PhonePictures.Decode("hello") is null && PhonePictures.Decode("data:image/jpeg;base64,***") is null, "");
        var folder = Path.Combine(Path.GetTempPath(), "spm-phone-pics-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = PhonePictures.Save(url, folder, DateTime.UtcNow);
            check("phone pictures: saved as a file the chat attaches", path is not null && File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes) && path.EndsWith(".jpg"), path ?? "");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }

        var t0 = new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);
        PhonePictures.Keep("chat:a", [1], t0.AddMinutes(-5));   // before the reply's turn: not sent
        PhonePictures.Keep(null, [2], t0.AddMinutes(1));        // the chat shown
        PhonePictures.Keep("chat:b", [3], t0.AddMinutes(1));    // another chat's
        PhonePictures.Keep("chat:a", [4], t0.AddMinutes(2));
        PhonePictures.Keep("chat:a", [5], t0.AddMinutes(3));
        var sent = PhonePictures.Take("chat:a", shown: true, t0);
        check("phone pictures: the reply takes its own turn's last two views", sent.Count == 2 && sent[0][0] == 4 && sent[1][0] == 5, string.Join(",", sent.Select(p => p[0])));
        check("phone pictures: taken once", PhonePictures.Take("chat:a", shown: true, t0).Count == 0, "");
        check("phone pictures: another chat keeps its own", PhonePictures.Take("chat:b", shown: false, t0).Count == 1, "");
    }

    /// <summary>
    /// 21 Nov 2026: the site took a question but didn't answer in time, so the PC thought it failed: it was sent again later
    /// (shown twice) or never (Q61 to Q65 never reached the phone). Now a message is sent again at once with the same id
    /// (cid), which Iris keeps once, and a tab question's id is the same each time it is sent.
    /// </summary>
    private static void Resend(Action<string, bool, string> check, byte[] key)
    {
        var was = IrisClient.RetryDelay;
        IrisClient.RetryDelay = _ => TimeSpan.Zero;
        try
        {
            var state = new IrisState { Site = "https://iris.test/iris", Key = IrisClient.B64u(key), Token = "t" };
            var slow = new Fake(2, 200);
            var id = new IrisClient(state, new HttpClient(slow)).AskAsync("Q4: 4 lengths or 3?", ["4", "3"], cid: "tab-1-4-a").GetAwaiter().GetResult();
            check("iris: a question the site didn't answer is sent again until it goes", id == 77 && slow.Calls == 3, $"id {id}, calls {slow.Calls}");
            check("iris: every try carries the same cid", slow.Cids.Distinct().Count() == 1 && slow.Cids[0] == "tab-1-4-a", string.Join(",", slow.Cids));

            var update = new Fake(1, 200);
            new IrisClient(state, new HttpClient(update)).UpdateAsync("Working on B9.").GetAwaiter().GetResult();
            check("iris: an update gets its own cid, kept on the retry", update.Cids.Count == 2 && update.Cids[0] == update.Cids[1] && update.Cids[0].StartsWith("t-"), string.Join(",", update.Cids));

            var refused = new Fake(0, 400);
            var threw = false;
            try { new IrisClient(state, new HttpClient(refused)).UpdateAsync("x").GetAwaiter().GetResult(); }
            catch (IrisException) { threw = true; }
            check("iris: a refusal isn't sent again", threw && refused.Calls == 1, $"calls {refused.Calls}");

            var down = new Fake(9, 200);
            threw = false;
            try { new IrisClient(state, new HttpClient(down)).UpdateAsync("x").GetAwaiter().GetResult(); }
            catch (HttpRequestException) { threw = true; }
            check("iris: tries end after three", threw && down.Calls == 3, $"calls {down.Calls}");
        }
        finally { IrisClient.RetryDelay = was; }

        var asked = new DateTime(2026, 11, 21, 15, 0, 0, DateTimeKind.Utc);
        var cid = SketchProMax.Chat.AskRules.TabCid("chat:abc", 61, asked);
        check("iris: a tab question has the same cid every time it is sent", cid == SketchProMax.Chat.AskRules.TabCid("chat:abc", 61, asked) && cid.Length <= 80, cid);
        check("iris: written again, it is a new message", cid != SketchProMax.Chat.AskRules.TabCid("chat:abc", 61, asked.AddMinutes(5)), "");
        check("iris: another chat's Q61 is another message", cid != SketchProMax.Chat.AskRules.TabCid("chat:xyz", 61, asked), "");
        var ask = new SketchProMax.Chat.UserAsk { Number = 61, Asked = asked };
        check("iris: a tab question's version is when it was asked", ask.Version == asked, "");
        ask.Rewritten = asked.AddMinutes(5);
        check("iris: or last written again", ask.Version == asked.AddMinutes(5), "");

        // 21 Nov 2026: four phone answers from one read went one by one, and the coordinator asked three of them again.
        check("phone answers: none left on the phone, they go together at once", SketchProMax.Chat.AskRules.PhoneDeliverAfter(0) == TimeSpan.Zero, "");
        check("phone answers: some still on the phone, they wait for them a while", SketchProMax.Chat.AskRules.PhoneDeliverAfter(2) == SketchProMax.Chat.AskRules.PhoneAnswerGather
            && SketchProMax.Chat.AskRules.PhoneAnswerGather <= TimeSpan.FromMinutes(2), "");
    }

    /// <summary>The site: the first <c>fail</c> calls are lost (no answer), then it answers <c>status</c>.</summary>
    private sealed class Fake(int fail, int status) : HttpMessageHandler
    {
        public int Calls;
        public readonly List<string> Cids = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Content is not null)
            {
                var body = System.Text.Json.Nodes.JsonNode.Parse(await request.Content.ReadAsStringAsync())!;
                Cids.Add(body["cid"]?.GetValue<string>() ?? "");
            }
            if (Calls <= fail) throw new HttpRequestException("The underlying connection was closed.");
            return new HttpResponseMessage((System.Net.HttpStatusCode)status)
            {
                Content = new StringContent(status == 200 ? "{\"id\": 77, \"created\": 1}" : "{\"error\": \"No.\"}"),
            };
        }
    }
}
