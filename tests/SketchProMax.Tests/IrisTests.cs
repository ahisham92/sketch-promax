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
    }
}
