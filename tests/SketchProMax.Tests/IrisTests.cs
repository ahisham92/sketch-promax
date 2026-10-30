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
    }
}
